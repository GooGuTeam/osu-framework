// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using osu.Framework.Audio;
using osu.Framework.Audio.Asio;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
            PreloadBass();
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        private long frameCount;
        // 标记当前是否处于 ASIO 活跃状态，防止重复 init/free 循环导致驱动不停 Play/Stop。
        private bool isAsioActive;
        // 当前 ASIO 设备索引
        private int currentAsioDeviceIndex = -1;
        // 统计次数用于诊断循环
        private int asioInitCount;
        private int asioStartCount;
        private int asioFreeCount;
        private long lastAsioAttemptTicks;
        private int asioConsecutiveFailures;
        private bool asioAttemptInProgress;
        private int asioInitGeneration; // 成功初始化次数(用于跨线程去抖)

        // WASAPI状态跟踪
        private bool isWasapiActive;
        private int currentWasapiDeviceIndex = -1;
        private int wasapiConsecutiveFailures;
        private int wasapiInitGeneration;
        private bool currentWasapiPreferExclusive = false; // 当前的模式首选项
        private volatile bool isWasapiInitializing = false; // 防止初始化期间的重复调用

        internal bool HasGlobalAsioMixer => globalMixerHandle.Value != null;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        private WasapiProcedure? wasapiProcedure;
        private WasapiNotifyProcedure? wasapiNotifyProcedure;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128)) // 128 == BASS_DEVICE_REINIT
                return false;

            // Need to do more testing. Users reporting buffer underruns even with a large (20ms) buffer.
            // Also playback latency improvements are not present across all users.
            // attemptWasapiInitialisation();

            initialised_devices.Add(deviceId);
            return true;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            // Check if the device being freed is an ASIO device
            bool isAsioDevice = false;
            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                var deviceInfo = Bass.GetDeviceInfo(deviceId);
                isAsioDevice = deviceInfo.Driver?.StartsWith("asio:", StringComparison.Ordinal) == true;
                Bass.Free();
            }

            freeWasapi();

            // If freeing an ASIO device, clean up ASIO resources
            if (isAsioDevice)
            {
                freeAsio();
            }

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

        public void attemptWasapiInitialisation()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (Bass.CurrentDevice > 0)
            {
                string driver = Bass.GetDeviceInfo(Bass.CurrentDevice).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    Logger.Log($"[AudioDebug] WASAPI: Looking for device matching Bass driver: {driver}", LoggingTarget.Runtime, LogLevel.Debug);

                    // In the normal execution case, BassWasapi.GetDeviceInfo will return false as soon as we reach the end of devices.
                    // This while condition is just a safety to avoid looping forever.
                    // It's intentionally quite high because if a user has many audio devices, this list can get long.
                    //
                    // Retrieving device info here isn't free. In the future we may want to investigate a better method.
                    while (wasapiDevice < 16384)
                    {
                        if (!BassWasapi.GetDeviceInfo(++wasapiDevice, out WasapiDeviceInfo info))
                            break;

                        if (info.ID == driver)
                        {
                            Logger.Log($"[AudioDebug] WASAPI: Found matching device {wasapiDevice}: {info.Name}", LoggingTarget.Runtime, LogLevel.Debug);
                            break;
                        }
                    }
                }
            }

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();
            initWasapi(wasapiDevice, preferExclusive: false); // 默认使用共享模式
        }

        /// <summary>
        /// 尝试初始化WASAPI独占模式。
        /// </summary>
        /// <param name="deviceIndex">设备索引，-1 表示默认输出设备</param>
        /// <returns>是否成功初始化独占模式</returns>
        public bool attemptWasapiExclusiveInitialisation(int deviceIndex = -1)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log($"[AudioDebug] WASAPI: Attempting exclusive mode initialization on device {deviceIndex}", LoggingTarget.Runtime, LogLevel.Debug);

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();

            initWasapi(deviceIndex, preferExclusive: true);

            return isWasapiActive && currentWasapiDeviceIndex == deviceIndex;
        }

        /// <summary>
        /// 获取WASAPI设备支持的最佳独占模式格式信息
        /// </summary>
        /// <param name="deviceIndex">设备索引</param>
        /// <returns>最佳格式信息，如果没有支持的格式则返回null</returns>
        public WasapiFormatInfo? getBestExclusiveFormat(int deviceIndex)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return null;

            if (!BassWasapi.GetDeviceInfo(deviceIndex, out WasapiDeviceInfo deviceInfo))
                return null;

            // 测试格式候选列表，按优先级排序
            var formatCandidates = new[]
            {
                // 专业音频格式 - 高采样率 + 低延迟
                (freq: 192000, ch: 2, desc: "192kHz/2ch (Professional)"),
                (freq: 96000, ch: 2, desc: "96kHz/2ch (High Quality)"),
                (freq: 48000, ch: 2, desc: "48kHz/2ch (Standard Professional)"),
                (freq: 44100, ch: 2, desc: "44.1kHz/2ch (CD Quality)"),

                // 多声道支持
                (freq: 48000, ch: deviceInfo.MixChannels, desc: $"48kHz/{deviceInfo.MixChannels}ch (Device Native)"),
                (freq: 44100, ch: deviceInfo.MixChannels, desc: $"44.1kHz/{deviceInfo.MixChannels}ch (Device Native)"),

                // 设备默认格式作为最后选择
                (freq: deviceInfo.MixFrequency, ch: deviceInfo.MixChannels, desc: $"{deviceInfo.MixFrequency}Hz/{deviceInfo.MixChannels}ch (Device Mix Format)")
            };

            foreach (var (freq, ch, desc) in formatCandidates)
            {
                var supportedFormat = BassWasapi.CheckFormat(deviceIndex, freq, ch, WasapiInitFlags.Exclusive);
                if (supportedFormat != WasapiFormat.Unknown)
                {
                    return new WasapiFormatInfo
                    {
                        Frequency = freq,
                        Channels = ch,
                        Format = supportedFormat,
                        Description = desc
                    };
                }
            }

            return null; // 没有支持的独占格式
        }

        /// <summary>
        /// WASAPI格式信息结构
        /// </summary>
        public struct WasapiFormatInfo
        {
            public int Frequency;
            public int Channels;
            public WasapiFormat Format;
            public string Description;

            public override string ToString() => Description;
        }


        public void initWasapi(int wasapiDevice, bool preferExclusive = false)
        {
            if (isWasapiInitializing)
                return;

            isWasapiInitializing = true;

            try
            {
                // 获取设备信息
                if (!BassWasapi.GetDeviceInfo(wasapiDevice, out WasapiDeviceInfo deviceInfo))
                {
                    isWasapiInitializing = false;
                    return;
                }

                // 设置当前模式首选项
                currentWasapiPreferExclusive = preferExclusive;
                setupWasapiCallbacks();

                // 确定初始化顺序：独占优先或共享优先
                var initModes = preferExclusive
                    ? new[] { WasapiInitFlags.Exclusive, WasapiInitFlags.Shared }
                    : new[] { WasapiInitFlags.Shared, WasapiInitFlags.Exclusive };

                foreach (var flags in initModes)
                {
                    if (tryInitializeWasapiMode(wasapiDevice, deviceInfo, flags))
                    {
                        isWasapiActive = true;
                        currentWasapiDeviceIndex = wasapiDevice;
                        wasapiConsecutiveFailures = 0;
                        wasapiInitGeneration++;
                        isWasapiInitializing = false;
                        return;
                    }
                }

                // 所有模式都失败，尝试最后的回退
                if (tryFallbackInit(wasapiDevice, deviceInfo))
                {
                    isWasapiActive = true;
                    currentWasapiDeviceIndex = wasapiDevice;
                    wasapiConsecutiveFailures = 0;
                    wasapiInitGeneration++;
                    isWasapiInitializing = false;
                    return;
                }

                // 完全失败
                wasapiConsecutiveFailures++;
                isWasapiActive = false;
                isWasapiInitializing = false;
            }
            catch
            {
                isWasapiActive = false;
                isWasapiInitializing = false;
            }
        }

        private bool tryInitializeWasapiMode(int wasapiDevice, WasapiDeviceInfo deviceInfo, WasapiInitFlags flags)
        {
            WasapiInitParams initParams;

            if (flags == WasapiInitFlags.Shared)
            {
                initParams = new WasapiInitParams(deviceInfo.MixFrequency, deviceInfo.MixChannels, 0.02f, 0.01f);
            }
            else
            {
                // 独占模式：尝试最兼容的格式
                initParams = findBestExclusiveFormat(wasapiDevice, deviceInfo);
                if (!initParams.IsValid)
                    return false;
            }

            return tryInitializeWasapi(wasapiDevice, initParams, flags);
        }

        private WasapiInitParams findBestExclusiveFormat(int wasapiDevice, WasapiDeviceInfo deviceInfo)
        {
            // 按兼容性优先级排序的格式列表
            var candidates = new[]
            {
                new WasapiInitParams(deviceInfo.MixFrequency, deviceInfo.MixChannels, 0.03f, 0.015f),
                new WasapiInitParams(48000, 2, 0.03f, 0.015f),
                new WasapiInitParams(44100, 2, 0.03f, 0.015f),
                new WasapiInitParams(deviceInfo.MixFrequency, deviceInfo.MixChannels, 0.05f, 0.025f),
                new WasapiInitParams(44100, 2, 0.05f, 0.025f),
            };

            foreach (var candidate in candidates)
            {
                if (BassWasapi.CheckFormat(wasapiDevice, candidate.Frequency, candidate.Channels, WasapiInitFlags.Exclusive) != WasapiFormat.Unknown)
                    return candidate;
            }

            return new WasapiInitParams(); // 无效
        }

        private bool tryFallbackInit(int wasapiDevice, WasapiDeviceInfo deviceInfo)
        {
            // 最后的回退：使用设备混音格式和大缓冲区的共享模式
            var fallbackParams = new WasapiInitParams(deviceInfo.MixFrequency, deviceInfo.MixChannels, 0.1f, 0.05f);
            return tryInitializeWasapi(wasapiDevice, fallbackParams, WasapiInitFlags.Shared);
        }

        private struct WasapiInitParams
        {
            public int Frequency;
            public int Channels;
            public float Buffer;
            public float Period;
            public bool IsValid => Frequency > 0 && Channels > 0;

            public WasapiInitParams(int frequency, int channels, float buffer = 0.02f, float period = 0.01f)
            {
                Frequency = frequency;
                Channels = channels;
                Buffer = buffer;
                Period = period;
            }
        }

        private void setupWasapiCallbacks()
        {
            // This is intentionally initialised inline and stored to a field.
            // If we don't do this, it gets GC'd away.
            wasapiProcedure = (buffer, length, _) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length | (int)DataFlags.Float);
            };

            wasapiNotifyProcedure = (notify, device, _) => Scheduler.Add(() =>
            {
                if (notify == WasapiNotificationType.DefaultOutput)
                {
                    // 防止在初始化过程中重复初始化
                    if (isWasapiActive && currentWasapiDeviceIndex >= 0 && !isWasapiInitializing)
                    {
                        freeWasapi();
                        initWasapi(device, currentWasapiPreferExclusive); // 保持当前的模式首选项
                    }
                }
            });
        }

        private bool tryInitializeWasapi(int wasapiDevice, WasapiInitParams initParams, WasapiInitFlags flags)
        {
            // 确保 BASS 解码上下文就绪
            if (!setupBassDecodeContext(flags))
                return false;

            // 初始化 WASAPI
            bool initialized = BassWasapi.Init(
                Device: wasapiDevice,
                Frequency: initParams.Frequency,
                Channels: initParams.Channels,
                Flags: flags,
                Buffer: initParams.Buffer,
                Period: initParams.Period,
                Procedure: wasapiProcedure,
                User: IntPtr.Zero
            );

            if (!initialized)
                return false;

            // 获取实际配置
            if (!BassWasapi.GetInfo(out var wasapiInfo))
            {
                BassWasapi.Free();
                return false;
            }

            // 创建全局混音器
            if (!createGlobalMixer(wasapiInfo))
            {
                BassWasapi.Free();
                return false;
            }

            // 启动设备
            if (!BassWasapi.Start())
            {
                if (globalMixerHandle.Value != null)
                {
                    Bass.StreamFree(globalMixerHandle.Value.Value);
                    globalMixerHandle.Value = null;
                }
                BassWasapi.Free();
                return false;
            }

            // 设置设备通知
            BassWasapi.SetNotify(wasapiNotifyProcedure);
            return true;
        }

        private bool setupBassDecodeContext(WasapiInitFlags flags)
        {
            // 确保完全释放现有 BASS 设备
            if (Bass.CurrentDevice >= 0)
                Bass.Free();

            // 使用 NoSound 设备作为解码上下文，避免与 WASAPI 冲突
            return Bass.Init(Bass.NoSoundDevice) || Bass.LastError == Errors.Already;
        }

        private bool createGlobalMixer(WasapiInfo wasapiInfo)
        {
            globalMixerHandle.Value = BassMix.CreateMixerStream(
                wasapiInfo.Frequency,
                wasapiInfo.Channels,
                BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float
            );

            return globalMixerHandle.Value != 0;
        }

        /// <summary>
        /// WASAPI状态信息结构
        /// </summary>
        public struct WasapiStatusInfo
        {
            public bool IsActive;
            public int DeviceIndex;
            public bool IsExclusive;
            public int Frequency;
            public int Channels;
            public WasapiFormat Format;
            public int BufferLength;
            public double CPUUsage;
            public bool IsStarted;

            public override string ToString()
            {
                var mode = IsExclusive ? "Exclusive" : "Shared";
                var latency = (BufferLength * 1000.0 / Frequency);
                return $"WASAPI {mode}: {Frequency}Hz/{Channels}ch/{Format.ToString()}, {latency:F1}ms latency, CPU: {CPUUsage:F1}%";
            }
        }
        private void freeWasapi()
        {
            Logger.Log("[AudioDebug] WASAPI: Freeing WASAPI resources", LoggingTarget.Runtime, LogLevel.Debug);
            try
            {
                // 停止WASAPI设备
                if (BassWasapi.IsStarted)
                {
                    Logger.Log("[AudioDebug] WASAPI: Stopping device", LoggingTarget.Runtime, LogLevel.Debug);
                    BassWasapi.Stop(true); // true = reset buffer
                }

                // 清除通知回调
                BassWasapi.SetNotify(null);

                // 释放全局混音器
                if (globalMixerHandle.Value != null)
                {
                    Logger.Log($"[AudioDebug] WASAPI: Freeing global mixer handle {globalMixerHandle.Value}", LoggingTarget.Runtime, LogLevel.Debug);
                    Bass.StreamFree(globalMixerHandle.Value.Value);
                    globalMixerHandle.Value = null;
                }

                // 释放WASAPI设备
                Logger.Log("[AudioDebug] WASAPI: Freeing WASAPI device", LoggingTarget.Runtime, LogLevel.Debug);
                BassWasapi.Free();

                // 清除回调函数引用
                wasapiProcedure = null;
                wasapiNotifyProcedure = null;

                // 重置状态
                isWasapiActive = false;
                currentWasapiDeviceIndex = -1;

                Logger.Log("[AudioDebug] WASAPI: Resources freed successfully", LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioDebug] WASAPI: Exception during cleanup: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);

                // 即使出现异常也要重置状态
                isWasapiActive = false;
                currentWasapiDeviceIndex = -1;
                globalMixerHandle.Value = null;
            }
        }

        internal void attemptAsioInitialisation(int asioDevice)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            if (asioAttemptInProgress)
            {
                Logger.Log("[AudioDebug] (AudioThread) ASIO attempt ignored (already in progress)", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }

            long nowTicks = Stopwatch.GetTimestamp();
            double secondsSinceLast = (nowTicks - lastAsioAttemptTicks) / (double)Stopwatch.Frequency;
            if (lastAsioAttemptTicks != 0 && secondsSinceLast < 1 && asioConsecutiveFailures > 0)
            {
                Logger.Log($"[AudioDebug] (AudioThread) ASIO attempt throttled ({secondsSinceLast:F2}s since last, failures={asioConsecutiveFailures})", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }
            lastAsioAttemptTicks = nowTicks;

            asioAttemptInProgress = true;
            try
            {
                // 如果已经完成过一次初始化（代号 generation>0）且 global mixer 仍在，本次请求直接跳过
                if (asioInitGeneration > 0 && globalMixerHandle.Value != null && currentAsioDeviceIndex == asioDevice)
                {
                    Logger.Log($"[AudioDebug] (AudioThread) Skip ASIO re-init (generation={asioInitGeneration}) device={asioDevice} mixer={globalMixerHandle.Value}", LoggingTarget.Runtime, LogLevel.Debug);
                    return;
                }

                // 只有在未活跃或 mixer 丢失时才重新初始化
                if (globalMixerHandle.Value != null)
                {
                    Logger.Log("[AudioDebug] (AudioThread) Global mixer present but isAsioActive=false, attempting safe reuse before full re-init.", LoggingTarget.Runtime, LogLevel.Debug);
                    // 再次尝试绑定现有 mixers（可能之前未成功）
                    setGlobalMixerHandleForAsio();
                    isAsioActive = true; // 视作已活跃
                    currentAsioDeviceIndex = asioDevice;
                    asioInitGeneration = Math.Max(asioInitGeneration, 1);
                    return;
                }

                if (currentAsioDeviceIndex != -1 && currentAsioDeviceIndex != asioDevice)
                    freeAsio();

                initAsio(asioDevice);
                if (!isAsioActive)
                {
                    asioConsecutiveFailures++;
                    Logger.Log($"[AudioDebug] (AudioThread) ASIO init result=FAIL (consecutiveFailures={asioConsecutiveFailures})", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else
                {
                    if (asioConsecutiveFailures > 0)
                        Logger.Log($"[AudioDebug] (AudioThread) ASIO init recovered after {asioConsecutiveFailures} failures", LoggingTarget.Runtime, LogLevel.Debug);
                    asioConsecutiveFailures = 0;
                }
            }
            finally
            {
                asioAttemptInProgress = false;
            }
        }

        private void initAsio(int asioDevice)
        {
            Logger.Log($"Attempting ASIO initialization for device {asioDevice}", LoggingTarget.Runtime, LogLevel.Debug);
            asioInitCount++;
            currentAsioDeviceIndex = asioDevice;
            Logger.Log($"[AudioDebug] (AudioThread) ASIO init count={asioInitCount}", LoggingTarget.Runtime, LogLevel.Debug);

            if (globalMixerHandle.Value != null)
            {
                Logger.Log("[AudioDebug] ASIO init aborted: global mixer already present.", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }

            // 在尝试 ASIO 之前确保没有残留 WASAPI / 普通 BASS 设备占用，以免 ASIO4ALL 标红“正在使用中”。
            freeWasapi();
            if (Bass.CurrentDevice >= 0 && Bass.CurrentDevice != Bass.NoSoundDevice)
            {
                Logger.Log($"[AudioDebug] Freeing existing standard BASS device {Bass.CurrentDevice} prior to ASIO init", LoggingTarget.Runtime, LogLevel.Debug);
                Bass.Free();
            }

            // Initialize the ASIO device using our manager
            if (AsioDeviceManager.InitializeDevice(asioDevice))
            {
                // Get device info
                var deviceInfo = AsioDeviceManager.GetCurrentDeviceInfo();
                if (deviceInfo.HasValue)
                {
                    Logger.Log($"ASIO device initialized: {deviceInfo.Value.Name}", LoggingTarget.Runtime, LogLevel.Important);

                    // 预先标记 isAsioActive 让 ensureBassDecodeContext 选择 NoSound 设备，避免抢占真实输出设备。
                    bool previousAsioActive = isAsioActive;
                    isAsioActive = true;
                    ensureBassDecodeContext();

                    // Start the device
                    if (AsioDeviceManager.StartDevice())
                    {
                        Logger.Log("ASIO device started successfully", LoggingTarget.Runtime, LogLevel.Important);

                        // 为避免 ASIO4ALL 显示底层 WDM 设备被本进程占用：
                        // 我们仅需要一个解码上下文，不需要继续保持对真实默认输出设备(如 index=1)的占用。
                        // 这里尝试将 Bass 解码上下文迁移到 NoSound (-1) 设备，释放系统设备。
                        switchToNoSoundDecodeContext();

                        // Set the global mixer handle for ASIO audio routing
                        setGlobalMixerHandleForAsio();

                        // 强制重建所有 AudioManager 下的 BassAudioMixer，确保 handle 有效
                        lock (managers)
                        {
                            foreach (var manager in managers)
                            {
                                manager.RecreateMixers();
                                // 立即触发新 mixer 的设备绑定与实际 BASS mixer 创建
                                int current = ManagedBass.Bass.CurrentDevice;
                                if (manager.TrackMixer is BassAudioMixer track && track.Handle == 0)
                                {
                                    Logger.Log($"[AudioDebug] (AudioThread) Forcing TrackMixer.UpdateDevice after ASIO init. CurrentDevice={current}", LoggingTarget.Runtime, LogLevel.Debug);
                                    track.UpdateDevice(current);
                                }
                                if (manager.SampleMixer is BassAudioMixer sample && sample.Handle == 0)
                                {
                                    Logger.Log($"[AudioDebug] (AudioThread) Forcing SampleMixer.UpdateDevice after ASIO init. CurrentDevice={current}", LoggingTarget.Runtime, LogLevel.Debug);
                                    sample.UpdateDevice(current);
                                }
                            }
                        }
                        isAsioActive = true; // 标记活跃，防止循环
                        asioStartCount++;
                        Logger.Log($"[AudioDebug] (AudioThread) ASIO start count={asioStartCount}", LoggingTarget.Runtime, LogLevel.Debug);
                        asioInitGeneration++;
                    }
                    else
                    {
                        Logger.Log("Failed to start ASIO device", LoggingTarget.Runtime, LogLevel.Error);

                        // Check for specific error codes and handle them appropriately
                        var error = BassAsio.LastError;
                        Logger.Log($"ASIO start failed with error: {error} (Code: {(int)error})", LoggingTarget.Runtime, LogLevel.Error);

                        // Handle BufferLost error (code 3) - common ASIO initialization issue
                        if (error == Errors.BufferLost)
                        {
                            Logger.Log("BufferLost error during ASIO start, this may indicate sample rate or buffer size compatibility issues",
                                      LoggingTarget.Runtime, LogLevel.Important);
                        }

                        // Handle Ended error (code 13) - this may indicate device was disconnected or stopped
                        if ((int)error == 13)
                        {
                            Logger.Log("ASIO device ended unexpectedly, attempting cleanup...", LoggingTarget.Runtime, LogLevel.Important);
                            freeAsio();
                        }
                        isAsioActive = previousAsioActive; // 启动失败还原状态
                    }
                }
            }
            else
            {
                Logger.Log("Failed to initialize ASIO device", LoggingTarget.Runtime, LogLevel.Error);

                // Log specific ASIO initialization error for better debugging
                var error = BassAsio.LastError;
                Logger.Log($"ASIO initialization failed with error: {error} (Code: {(int)error})", LoggingTarget.Runtime, LogLevel.Error);

                // If initialization fails, we should not proceed with further operations
                return;
            }
        }

        /// <summary>
        /// 在 ASIO 成功启动后，将普通 BASS 设备释放并改为使用 NoSound 设备，仅用于解码，避免占用实际输出设备 (ASIO4ALL 会显示 WDM 设备被占用的问题)。
        /// </summary>
        private void switchToNoSoundDecodeContext()
        {
            try
            {
                int current = Bass.CurrentDevice;
                if (current != Bass.NoSoundDevice && current >= 0)
                {
                    Logger.Log($"[AudioDebug] Switching decode context from device {current} to NoSound (-1) for ASIO mode", LoggingTarget.Runtime, LogLevel.Debug);
                    // 释放当前普通设备
                    Bass.Free();
                    // 初始化 NoSound 设备用于后续解码
                    if (!Bass.Init(Bass.NoSoundDevice))
                    {
                        Logger.Log($"[AudioDebug] Bass.Init(NoSound) failed: {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    }
                    else
                    {
                        Logger.Log("[AudioDebug] Bass.Init(NoSound) success (decode-only)", LoggingTarget.Runtime, LogLevel.Debug);
                    }
                }
                else
                {
                    Logger.Log("[AudioDebug] Decode context already on NoSound or invalid; no switch needed", LoggingTarget.Runtime, LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioDebug] switchToNoSoundDecodeContext exception: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// Sets the global mixer handle for ASIO audio routing.
        /// This allows the ASIO callback to access the game's audio data.
        /// </summary>
        private void setGlobalMixerHandleForAsio()
        {
            // Create a global mixer for ASIO if one doesn't exist
            if (globalMixerHandle.Value == null)
            {
                // Create a global mixer stream for ASIO with proper configuration
                int mixerHandle = BassMix.CreateMixerStream(44100, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
                if (mixerHandle != 0)
                {
                    globalMixerHandle.Value = mixerHandle;
                    Logger.Log("Created global mixer for ASIO audio routing", LoggingTarget.Runtime, LogLevel.Important);

                    // Set buffer attributes for low latency
                    Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Buffer, 0);

                    // 解码 mixer 不需要 ChannelPlay；数据通过 ChannelGetData 主动拉取
                    // Bass.ChannelPlay(mixerHandle); // removed for decode-only mixer

                    // Set the global mixer handle in the ASIO device manager
                    AsioDeviceManager.SetGlobalMixerHandle(mixerHandle);
                }
                else
                {
                    Logger.Log($"Failed to create global mixer for ASIO. LastError={Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    ensureBassDecodeContext(force: true);
                    mixerHandle = BassMix.CreateMixerStream(44100, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
                    if (mixerHandle == 0)
                    {
                        Logger.Log($"[AudioDebug] Retry still failed to create global mixer. LastError={Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                        return;
                    }
                    globalMixerHandle.Value = mixerHandle;
                    Logger.Log("[AudioDebug] Retry created global mixer after ensuring decode context.", LoggingTarget.Runtime, LogLevel.Important);
                    Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Buffer, 0);
                    // decode-only
                    AsioDeviceManager.SetGlobalMixerHandle(mixerHandle);
                }
            }
            else
            {
                // Use existing global mixer handle
                AsioDeviceManager.SetGlobalMixerHandle(globalMixerHandle.Value.Value);
            }

            // Add all existing audio managers' mixers to the global ASIO mixer
            lock (managers)
            {
                foreach (var manager in managers)
                {
                    // Connect the manager's track mixer to the global ASIO mixer
                    if (manager.TrackMixer is BassAudioMixer bassTrackMixer && bassTrackMixer.Handle != 0 && globalMixerHandle.Value.HasValue)
                    {
                        bool result = BassMix.MixerAddChannel(globalMixerHandle.Value.Value, bassTrackMixer.Handle,
                                                  BassFlags.MixerChanBuffer | BassFlags.MixerChanNoRampin);
                        var lastError = Bass.LastError;
                        if (!result && lastError == Errors.Already)
                        {
                            Logger.Log($"[AudioDebug] Track mixer already attached to global mixer, skipping duplicate. MixerHandle={globalMixerHandle.Value.Value}, TrackMixerHandle={bassTrackMixer.Handle}", LoggingTarget.Runtime, LogLevel.Debug);
                        }
                        else
                        {
                            Logger.Log($"[AudioDebug] Try connect track mixer to ASIO global mixer: MixerHandle={globalMixerHandle.Value.Value}, TrackMixerHandle={bassTrackMixer.Handle}, Result={result}, LastError={lastError}", LoggingTarget.Runtime, result ? LogLevel.Debug : LogLevel.Error);
                        }
                    }

                    // Connect the manager's sample mixer to the global ASIO mixer
                    if (manager.SampleMixer is BassAudioMixer bassSampleMixer && bassSampleMixer.Handle != 0 && globalMixerHandle.Value.HasValue)
                    {
                        bool result = BassMix.MixerAddChannel(globalMixerHandle.Value.Value, bassSampleMixer.Handle,
                                                  BassFlags.MixerChanBuffer | BassFlags.MixerChanNoRampin);
                        var lastError = Bass.LastError;
                        if (!result && lastError == Errors.Already)
                        {
                            Logger.Log($"[AudioDebug] Sample mixer already attached to global mixer, skipping duplicate. MixerHandle={globalMixerHandle.Value.Value}, SampleMixerHandle={bassSampleMixer.Handle}", LoggingTarget.Runtime, LogLevel.Debug);
                        }
                        else
                        {
                            Logger.Log($"[AudioDebug] Try connect sample mixer to ASIO global mixer: MixerHandle={globalMixerHandle.Value.Value}, SampleMixerHandle={bassSampleMixer.Handle}, Result={result}, LastError={lastError}", LoggingTarget.Runtime, result ? LogLevel.Debug : LogLevel.Error);
                        }
                    }
                }
            }

            // Ensure the global mixer is playing to process audio data
            if (globalMixerHandle.Value.HasValue)
            {
                Bass.ChannelPlay(globalMixerHandle.Value.Value);
            }
        }

        /// <summary>
        /// 确保存在一个可用的 Bass 设备用于解码/混音。按顺序尝试: 真实默认(1)->NoSound(-1)->设备0。
        /// </summary>
        private void ensureBassDecodeContext(bool force = false)
        {
            if (!force && Bass.CurrentDevice != -1 && Bass.GetDeviceInfo(Bass.CurrentDevice, out var info) && info.IsInitialized)
            {
                Logger.Log($"[AudioDebug] ensureBassDecodeContext: existing device ok (Device={Bass.CurrentDevice})", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }

            // 在 ASIO 模式下优先使用 NoSound 设备，避免重新占用 WDM 输出设备导致 ASIO4ALL 显示“已占用”。
            if (isAsioActive)
            {
                if (Bass.Init(Bass.NoSoundDevice))
                {
                    Logger.Log("[AudioDebug] ensureBassDecodeContext(ASIO): Bass.Init(NoSound) success", LoggingTarget.Runtime, LogLevel.Debug);
                    return;
                }
                Logger.Log($"[AudioDebug] ensureBassDecodeContext(ASIO): Bass.Init(NoSound) failed {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

                // 最后兜底尝试设备0（理论上不会需要，但保留）
                if (Bass.Init(0))
                {
                    Logger.Log("[AudioDebug] ensureBassDecodeContext(ASIO): Bass.Init(0) success (fallback)", LoggingTarget.Runtime, LogLevel.Debug);
                    return;
                }
                Logger.Log($"[AudioDebug] ensureBassDecodeContext(ASIO): Bass.Init(0) failed {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                return;
            }

            if (Bass.Init(1))
            {
                Logger.Log("[AudioDebug] ensureBassDecodeContext: Bass.Init(1) success", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }
            Logger.Log($"[AudioDebug] ensureBassDecodeContext: Bass.Init(1) failed {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

            if (Bass.Init(Bass.NoSoundDevice))
            {
                Logger.Log("[AudioDebug] ensureBassDecodeContext: Bass.Init(NoSound) success", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }
            Logger.Log($"[AudioDebug] ensureBassDecodeContext: Bass.Init(NoSound) failed {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

            if (Bass.Init(0))
            {
                Logger.Log("[AudioDebug] ensureBassDecodeContext: Bass.Init(0) success", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }
            Logger.Log($"[AudioDebug] ensureBassDecodeContext: Bass.Init(0) failed {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
        }

        private void freeAsio()
        {
            AsioDeviceManager.FreeDevice();
            asioFreeCount++;
            Logger.Log($"[AudioDebug] (AudioThread) ASIO free count={asioFreeCount}", LoggingTarget.Runtime, LogLevel.Debug);

            // Reset the global mixer handle to ensure audio managers are not bound to ASIO mixer anymore
            if (globalMixerHandle.Value != null)
            {
                Bass.StreamFree(globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
            }
            isAsioActive = false;
        }

        #endregion
    }
}

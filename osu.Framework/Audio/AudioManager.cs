// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Fx;
using ManagedBass.Mix;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Stores;
using osu.Framework.Audio.Asio;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using osu.Framework.Threading;
using ManagedBass.Wasapi;

namespace osu.Framework.Audio
{
    public class AudioManager : AudioCollectionManager<AudioComponent>
    {
        /// <summary>
        /// The number of BASS audio devices preceding the first real audio device.
        /// Consisting of <see cref="Bass.NoSoundDevice"/> and <see cref="bass_default_device"/>.
        /// </summary>
        protected const int BASS_INTERNAL_DEVICE_COUNT = 2;

        /// <summary>
        /// The index of the BASS audio device denoting the OS default.
        /// </summary>
        /// <remarks>
        /// See http://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_DEFAULT.html for more information on the included device.
        /// </remarks>
        private const int bass_default_device = 1;

        /// <summary>
        /// The manager component responsible for audio tracks (e.g. songs).
        /// </summary>
        public ITrackStore Tracks => globalTrackStore.Value;

        /// <summary>
        /// The manager component responsible for audio samples (e.g. sound effects).
        /// </summary>
        public ISampleStore Samples => globalSampleStore.Value;

        /// <summary>
        /// The thread audio operations (mainly Bass calls) are ran on.
        /// </summary>
        private readonly AudioThread thread;

        /// <summary>
        /// The global mixer which all tracks are routed into by default.
        /// </summary>
        public AudioMixer TrackMixer { get; private set; }

        /// <summary>
        /// The global mixer which all samples are routed into by default.
        /// </summary>
        public AudioMixer SampleMixer { get; private set; }
        /// <summary>
        /// 重新创建 TrackMixer 和 SampleMixer。
        /// </summary>
        private bool isAsioActive; // ASIO 模式标记
        private bool asioMixersInitialised; // 防止重复重建
        private bool asioDuplicateLogged; // 记录已打印过重复 setAudioDevice 堆栈
        private string lastAppliedDevice; // 记录最近一次真正应用的设备名用于 debounce
        public void RecreateMixers()
        {
            // 新增：如果全局 GlobalMixerHandle 为 0，说明 WASAPI/ASIO 初始化失败，直接报错并阻断重建，避免死循环
            if (GlobalMixerHandle.Value == 0)
            {
                Logger.Log("[AudioDebug] RecreateMixers aborted: GlobalMixerHandle is 0. WASAPI/ASIO init failed. Please check device compatibility or try shared mode.", LoggingTarget.Runtime, LogLevel.Error);
                return;
            }

            if (isAsioActive && asioMixersInitialised)
            {
                Logger.Log("[AudioDebug] Skip RecreateMixers (ASIO already initialised)", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }

            var oldTrackMixer = TrackMixer as Mixing.Bass.BassAudioMixer;
            var oldSampleMixer = SampleMixer as Mixing.Bass.BassAudioMixer;

            TrackMixer?.Dispose();
            TrackMixer = createAudioMixer(null, nameof(TrackMixer));
            Logger.Log("[AudioDebug] Recreated TrackMixer after device change.", LoggingTarget.Runtime, LogLevel.Debug);
            SampleMixer?.Dispose();
            SampleMixer = createAudioMixer(null, nameof(SampleMixer));
            Logger.Log("[AudioDebug] Recreated SampleMixer after device change.", LoggingTarget.Runtime, LogLevel.Debug);

            if (TrackMixer is Mixing.Bass.BassAudioMixer trackBassMixer)
                trackBassMixer.EnsureCreated();
            if (SampleMixer is Mixing.Bass.BassAudioMixer sampleBassMixer)
                sampleBassMixer.EnsureCreated();

            // 迁移已有的 Track / Sample 到新 mixer，防止 activeChannels 变 0。
            try
            {
                // 迁移 Track: 遍历所有存储的 TrackStore（globalTrackStore 以及其子 TrackStore）
                if (globalTrackStore.IsValueCreated)
                {
                    foreach (var storeTrack in globalTrackStore.Value.Items.OfType<Track.TrackBass>())
                    {
                        var channelInterface = (IAudioChannel)storeTrack;
                        if (channelInterface.Mixer == oldTrackMixer && TrackMixer is AudioMixer newTm)
                        {
                            // 强制重新绑定
                            channelInterface.Mixer = newTm;
                            Logger.Log($"[AudioDebug] Migrated Track '{storeTrack}' to new TrackMixer.", LoggingTarget.Runtime, LogLevel.Debug);
                        }
                    }
                }
                // 迁移 Sample: 遍历 SampleStore 中所有 SampleChannel (若具备 Bass 通道)——此处简单处理：对 SampleMixer 无法直接枚举具体 sample channel，样本通常在播放时创建，可忽略。
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioDebug] Mixer migration exception: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }

            // 如果已存在全局 mixer（ASIO 或 WASAPI exclusive），需要把新建的 Track/Sample mixer 重新挂载，否则 global activeChannels 会保持 0。
            if (GlobalMixerHandle.Value.HasValue)
            {
                int globalHandle = GlobalMixerHandle.Value.Value;
                if (TrackMixer is Mixing.Bass.BassAudioMixer tbm && tbm.Handle != 0)
                {
                    bool added = ManagedBass.Mix.BassMix.MixerAddChannel(globalHandle, tbm.Handle, ManagedBass.BassFlags.MixerChanBuffer | ManagedBass.BassFlags.MixerChanNoRampin);
                    if (added)
                        Logger.Log($"[AudioDebug] Reattach TrackMixer -> GlobalMixer. TrackHandle={tbm.Handle} Global={globalHandle}", LoggingTarget.Runtime, LogLevel.Debug);
                    else
                        Logger.Log($"[AudioDebug] Failed reattach TrackMixer -> GlobalMixer. TrackHandle={tbm.Handle} Global={globalHandle} LastError={ManagedBass.Bass.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                }
                if (SampleMixer is Mixing.Bass.BassAudioMixer sbm && sbm.Handle != 0)
                {
                    bool added = ManagedBass.Mix.BassMix.MixerAddChannel(globalHandle, sbm.Handle, ManagedBass.BassFlags.MixerChanBuffer | ManagedBass.BassFlags.MixerChanNoRampin);
                    if (added)
                        Logger.Log($"[AudioDebug] Reattach SampleMixer -> GlobalMixer. SampleHandle={sbm.Handle} Global={globalHandle}", LoggingTarget.Runtime, LogLevel.Debug);
                    else
                        Logger.Log($"[AudioDebug] Failed reattach SampleMixer -> GlobalMixer. SampleHandle={sbm.Handle} Global={globalHandle} LastError={ManagedBass.Bass.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                }
            }

            if (isAsioActive && !asioMixersInitialised)
                asioMixersInitialised = true;
        }

        /// <summary>
        /// The names of all available audio devices.
        /// </summary>
        /// <remarks>
        /// This property does not contain the names of disabled audio devices.
        /// </remarks>
        public IEnumerable<string> AudioDeviceNames => audioDeviceNames;

        /// <summary>
        /// Is fired whenever a new audio device is discovered and provides its name.
        /// </summary>
        public event Action<string> OnNewDevice;

        /// <summary>
        /// Is fired whenever an audio device is lost and provides its name.
        /// </summary>
        public event Action<string> OnLostDevice;

        /// <summary>
        /// The preferred audio device we should use. A value of
        /// <see cref="string.Empty"/> denotes the OS default.
        /// </summary>
        public readonly Bindable<string> AudioDevice = new Bindable<string>();

        /// <summary>
        /// The audio device buffer length in milliseconds.
        /// Lower values reduce latency but may cause audio stuttering.
        /// </summary>
        public readonly BindableDouble AudioDeviceBufferLength = new BindableDouble(10)
        {
            MinValue = 1,
            MaxValue = 100
        };

        /// <summary>
        /// Volume of all samples played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeSample = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Volume of all tracks played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeTrack = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Whether a global mixer is being used for audio routing.
        /// For now, this is only the case on Windows when using shared mode WASAPI initialisation.
        /// </summary>
        public IBindable<bool> UsingGlobalMixer => usingGlobalMixer;

        private readonly Bindable<bool> usingGlobalMixer = new BindableBool();

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        /// <remarks>
        /// When this is non-null, all mixers created via <see cref="CreateAudioMixer"/>
        /// will themselves be added to the global mixer, which will handle playback itself.
        ///
        /// In this mode of operation, nested mixers will be created with the <see cref="BassFlags.Decode"/>
        /// flag, meaning they no longer handle playback directly.
        ///
        /// An eventual goal would be to use a global mixer across all platforms as it can result
        /// in more control and better playback performance.
        /// </remarks>
        internal readonly IBindable<int?> GlobalMixerHandle = new Bindable<int?>();

        public override bool IsLoaded => base.IsLoaded &&
                                         // bass default device is a null device (-1), not the actual system default.
                                         Bass.CurrentDevice != Bass.DefaultDevice;

        // Mutated by multiple threads, must be thread safe.
        private ImmutableArray<DeviceInfo> audioDevices = ImmutableArray<DeviceInfo>.Empty;
        private ImmutableList<string> audioDeviceNames = ImmutableList<string>.Empty;

        private bool wasapiExclusiveInitialised = false;

        private Scheduler scheduler => thread.Scheduler;

        private Scheduler eventScheduler => EventScheduler ?? scheduler;

        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        /// <summary>
        /// The scheduler used for invoking publicly exposed delegate events.
        /// </summary>
        public Scheduler EventScheduler;

        internal IBindableList<AudioMixer> ActiveMixers => activeMixers;
        private readonly BindableList<AudioMixer> activeMixers = new BindableList<AudioMixer>();

        private readonly Lazy<TrackStore> globalTrackStore;
        private readonly Lazy<SampleStore> globalSampleStore;

        /// <summary>
        /// Constructs an AudioStore given a track resource store, and a sample resource store.
        /// </summary>
        /// <param name="audioThread">The host's audio thread.</param>
        /// <param name="trackStore">The resource store containing all audio tracks to be used in the future.</param>
        /// <param name="sampleStore">The sample store containing all audio samples to be used in the future.</param>
        public AudioManager(AudioThread audioThread, ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore)
        {
            thread = audioThread;

            thread.RegisterManager(this);

            AudioDevice.ValueChanged += _ => onDeviceChanged();
            AudioDeviceBufferLength.ValueChanged += _ => applyAudioSettings();
            GlobalMixerHandle.ValueChanged += handle =>
            {
                onDeviceChanged();
                usingGlobalMixer.Value = handle.NewValue.HasValue;
            };

            AddItem(TrackMixer = createAudioMixer(null, nameof(TrackMixer)));
            AddItem(SampleMixer = createAudioMixer(null, nameof(SampleMixer)));

            globalTrackStore = new Lazy<TrackStore>(() =>
            {
                var store = new TrackStore(trackStore, TrackMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeTrack);
                return store;
            });

            globalSampleStore = new Lazy<SampleStore>(() =>
            {
                var store = new SampleStore(sampleStore, SampleMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeSample);
                return store;
            });

            CancellationToken token = cancelSource.Token;

            syncAudioDevices();

        }

        protected override void Dispose(bool disposing)
        {
            cancelSource.Cancel();

            thread.UnregisterManager(this);

            OnNewDevice = null;
            OnLostDevice = null;

            base.Dispose(disposing);
        }

        private void onDeviceChanged()
        {
            string target = AudioDevice.Value;
            if (isAsioActive && GlobalMixerHandle.Value != null && target == lastAppliedDevice && target != null && target.StartsWith("ASIO: ", StringComparison.Ordinal))
                return; // debounce identical ASIO device while active

            scheduler.Add(() =>
            {
                if (setAudioDevice(target))
                    lastAppliedDevice = target;
            });
        }

        private void applyAudioSettings()
        {
            scheduler.Add(() =>
            {
                // Apply audio buffer length settings when they change
                Bass.DeviceBufferLength = (int)AudioDeviceBufferLength.Value;
            });
        }

        private void onDevicesChanged()
        {
            scheduler.Add(() =>
            {
                if (cancelSource.IsCancellationRequested)
                    return;

                if (!IsCurrentDeviceValid())
                    setAudioDevice();
            });
        }

        private static int userMixerID;

        /// <summary>
        /// Creates a new <see cref="AudioMixer"/>.
        /// </summary>
        /// <remarks>
        /// Channels removed from this <see cref="AudioMixer"/> fall back to the global <see cref="SampleMixer"/>.
        /// </remarks>
        /// <param name="identifier">An identifier displayed on the audio mixer visualiser.</param>
        public AudioMixer CreateAudioMixer(string identifier = default) =>
            createAudioMixer(SampleMixer, !string.IsNullOrEmpty(identifier) ? identifier : $"user #{Interlocked.Increment(ref userMixerID)}");

        private AudioMixer createAudioMixer(AudioMixer fallbackMixer, string identifier)
        {
            var mixer = new BassAudioMixer(this, fallbackMixer, identifier);
            AddItem(mixer);
            return mixer;
        }

        protected override void ItemAdded(AudioComponent item)
        {
            base.ItemAdded(item);
            if (item is AudioMixer mixer)
                activeMixers.Add(mixer);
        }

        protected override void ItemRemoved(AudioComponent item)
        {
            base.ItemRemoved(item);
            if (item is AudioMixer mixer)
                activeMixers.Remove(mixer);
        }

        /// <summary>
        /// Obtains the <see cref="TrackStore"/> corresponding to a given resource store.
        /// Returns the global <see cref="TrackStore"/> if no resource store is passed.
        /// </summary>
        /// <param name="store">The <see cref="IResourceStore{T}"/> of which to retrieve the <see cref="TrackStore"/>.</param>
        /// <param name="mixer">The <see cref="AudioMixer"/> to use for tracks created by this store. Defaults to the global <see cref="TrackMixer"/>.</param>
        public ITrackStore GetTrackStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null)
        {
            if (store == null) return globalTrackStore.Value;

            TrackStore tm = new TrackStore(store, mixer ?? TrackMixer);
            globalTrackStore.Value.AddItem(tm);
            return tm;
        }

        /// <summary>
        /// Obtains the <see cref="SampleStore"/> corresponding to a given resource store.
        /// Returns the global <see cref="SampleStore"/> if no resource store is passed.
        /// </summary>
        /// <remarks>
        /// By default, <c>.wav</c> and <c>.ogg</c> extensions will be automatically appended to lookups on the returned store
        /// if the lookup does not correspond directly to an existing filename.
        /// Additional extensions can be added via <see cref="ISampleStore.AddExtension"/>.
        /// </remarks>
        /// <param name="store">The <see cref="IResourceStore{T}"/> of which to retrieve the <see cref="SampleStore"/>.</param>
        /// <param name="mixer">The <see cref="AudioMixer"/> to use for samples created by this store. Defaults to the global <see cref="SampleMixer"/>.</param>
        public ISampleStore GetSampleStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null)
        {
            if (store == null) return globalSampleStore.Value;

            SampleStore sm = new SampleStore(store, mixer ?? SampleMixer);
            globalSampleStore.Value.AddItem(sm);
            return sm;
        }

        /// <summary>
        /// Sets the output audio device by its name.
        /// This will automatically fall back to the system default device on failure.
        /// </summary>
        /// <param name="deviceName">Name of the audio device, or null to use the configured device preference <see cref="AudioDevice"/>.</param>
        private bool setAudioDevice(string deviceName = null)
        {
            deviceName ??= AudioDevice.Value;

            if (deviceName != null && deviceName.StartsWith("ASIO: ", StringComparison.Ordinal) && isAsioActive && GlobalMixerHandle.Value != null)
            {
                if (!asioDuplicateLogged)
                {
                    var st = new StackTrace(1, true);
                    Logger.Log($"[AudioDebug] (AudioManager) Suppress repeat setAudioDevice on ASIO (deviceName='{deviceName}'). Stack (logged once):\n{st}", LoggingTarget.Runtime, LogLevel.Debug);
                    asioDuplicateLogged = true;
                }
                return true; // 已经处于 ASIO
            }

            // Check if this is an ASIO device
            if (deviceName?.StartsWith("ASIO: ", StringComparison.Ordinal) == true)
            {
                string asioDeviceName = deviceName[6..]; // Remove "ASIO: " prefix
                var asioDevices = AsioDeviceManager.AvailableDevices.ToList();
                for (int i = 0; i < asioDevices.Count; i++)
                {
                    if (asioDevices[i].Name == asioDeviceName)
                    {
                        return setAudioDeviceAsio(i);
                    }
                }
                return false;
            }

            // Check if this is a WASAPI device
            if (deviceName?.StartsWith("WASAPI Exclusive: ", StringComparison.Ordinal) == true)
            {
                string wasapiDeviceName = deviceName[18..]; // Remove "WASAPI Exclusive: " prefix
                return setAudioDeviceWasapi(wasapiDeviceName, true);
            }

            if (deviceName?.StartsWith("WASAPI Shared: ", StringComparison.Ordinal) == true)
            {
                string wasapiDeviceName = deviceName[15..]; // Remove "WASAPI Shared: " prefix
                return setAudioDeviceWasapi(wasapiDeviceName, false);
            }

            // try using the specified device
            int deviceIndex = audioDeviceNames.FindIndex(d => d == deviceName);
            if (deviceIndex >= 0 && setAudioDevice(BASS_INTERNAL_DEVICE_COUNT + deviceIndex))
                return true;

            // try using the system default if there is any device present.
            if (audioDeviceNames.Count > 0 && setAudioDevice(bass_default_device))
                return true;

            // no audio devices can be used, so try using Bass-provided "No sound" device as last resort.
            if (setAudioDevice(Bass.NoSoundDevice))
                return true;

            // we're boned. even "No sound" device won't initialise.
            return false;
        }

        private bool setAudioDevice(int deviceIndex)
        {
            var device = audioDevices.ElementAtOrDefault(deviceIndex);

            // device is invalid
            if (!device.IsEnabled)
                return false;

            // we don't want bass initializing with real audio device on headless test runs.
            if (deviceIndex != Bass.NoSoundDevice && DebugUtils.IsNUnitRunning)
                return false;

            // Check if this is an ASIO device
            if (device.Driver?.StartsWith("asio:", StringComparison.Ordinal) == true)
            {
                // Handle ASIO device initialization
                if (int.TryParse(device.Driver.AsSpan(5), out int asioDeviceIndex))
                {
                    return InitAsio(asioDeviceIndex);
                }
                return false;
            }

            // initialize new device
            bool initSuccess = InitBass(deviceIndex);
            if (Bass.LastError != Errors.Already && BassUtils.CheckFaulted(false))
                return false;

            if (!initSuccess)
            {
                Logger.Log("BASS failed to initialize but did not provide an error code", level: LogLevel.Error);
                return false;
            }

            Logger.Log($@"🔈 BASS initialised
                          BASS version:           {Bass.Version}
                          BASS FX version:        {BassFx.Version}
                          BASS MIX version:       {BassMix.Version}
                          Device:                 {device.Name}
                          Driver:                 {device.Driver}
                          Update period:          {Bass.UpdatePeriod} ms
                          Device buffer length:   {Bass.DeviceBufferLength} ms
                          Playback buffer length: {Bass.PlaybackBufferLength} ms");

            //we have successfully initialised a new device.
            UpdateDevice(deviceIndex);

            return true;
        }

        /// <summary>
        /// This method calls <see cref="Bass.Init(int, int, DeviceInitFlags, IntPtr, IntPtr)"/>.
        /// It can be overridden for unit testing.
        /// </summary>
        protected virtual bool InitBass(int device)
        {
            if (Bass.CurrentDevice == device)
                return true;

            // this likely doesn't help us but also doesn't seem to cause any issues or any cpu increase.
            Bass.UpdatePeriod = 1;

            // reduce latency to a known sane minimum.
            Bass.DeviceBufferLength = (int)AudioDeviceBufferLength.Value;
            Bass.PlaybackBufferLength = 100;

            // ensure there are no brief delays on audio operations (causing stream stalls etc.) after periods of silence.
            Bass.DeviceNonStop = true;

            // without this, if bass falls back to directsound legacy mode the audio playback offset will be way off.
            Bass.Configure(ManagedBass.Configuration.TruePlayPosition, 0);

            // Set BASS_IOS_SESSION_DISABLE here to leave session configuration in our hands (see iOS project).
            Bass.Configure(ManagedBass.Configuration.IOSSession, 16);

            // Always provide a default device. This should be a no-op, but we have asserts for this behaviour.
            Bass.Configure(ManagedBass.Configuration.IncludeDefaultDevice, true);

            // Enable custom BASS_CONFIG_MP3_OLDGAPS flag for backwards compatibility.
            Bass.Configure((ManagedBass.Configuration)68, 1);

            // Disable BASS_CONFIG_DEV_TIMEOUT flag to keep BASS audio output from pausing on device processing timeout.
            // See https://www.un4seen.com/forum/?topic=19601 for more information.
            Bass.Configure((ManagedBass.Configuration)70, false);

            if (!thread.InitDevice(device))
                return false;

            return true;
        }

        /// <summary>
        /// Initializes ASIO device.
        /// </summary>
        /// <param name="asioDeviceIndex">The index of the ASIO device to initialize.</param>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        protected virtual bool InitAsio(int asioDeviceIndex)
        {
            if (isAsioActive && GlobalMixerHandle.Value != null)
            {
                if (!asioDuplicateLogged)
                {
                    Logger.Log($"[AudioDebug] (AudioManager) Skip InitAsio duplicate. deviceIndex={asioDeviceIndex} globalMixer={GlobalMixerHandle.Value}", LoggingTarget.Runtime, LogLevel.Debug);
                    asioDuplicateLogged = true;
                }
                return true; // 已经初始化
            }
            // 统一走 AudioThread 的初始化流程，避免与此处重复实现导致 Bass 未 Init。
            Logger.Log($"[AudioDebug] (AudioManager) Delegating ASIO init to AudioThread for device {asioDeviceIndex}", LoggingTarget.Runtime, LogLevel.Debug);

            try
            {
                // 使用 AudioThread 内的 attemptAsioInitialisation，它内部：
                // 1. freeAsio()
                // 2. initAsio() -> AsioDeviceManager.InitializeDevice(asioDeviceIndex)
                // 3. 成功后调用 Bass.Init(0)（我们在 AudioThread.initAsio 中已加入）
                // 4. 创建全局 mixer 并绑定到各 AudioManager.GlobalMixerHandle
                // 5. RecreateMixers() 重建所有 manager 的 Track/Sample Mixer
                thread.attemptAsioInitialisation(asioDeviceIndex);

                // 验证 ASIO 设备是否成功
                var info = AsioDeviceManager.GetCurrentDeviceInfo();
                if (info == null)
                {
                    var error = BassAsio.LastError;
                    Logger.Log($"[AudioDebug] (AudioManager) ASIO init via AudioThread failed. LastError={error} ({(int)error})", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // 确保 Bass 已初始化（保险：如果 AudioThread 中因某种原因未成功 Init，这里补一次）
                if (Bass.CurrentDevice == -1)
                {
                    if (!Bass.Init(0))
                    {
                        Logger.Log($"[AudioDebug] (AudioManager) Fallback Bass.Init(0) failed. LastError={Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                        return false;
                    }
                    Logger.Log("[AudioDebug] (AudioManager) Fallback Bass.Init(0) success.", LoggingTarget.Runtime, LogLevel.Debug);
                }

                isAsioActive = true;
                Logger.Log($"[AudioDebug] (AudioManager) ASIO init complete. CurrentDevice={Bass.CurrentDevice}, GlobalMixer={(GlobalMixerHandle.Value?.ToString() ?? "null")}", LoggingTarget.Runtime, LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioDebug] (AudioManager) Exception delegating ASIO init: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                var asioError = BassAsio.LastError;
                if (asioError != Errors.OK)
                    Logger.Log($"[AudioDebug] (AudioManager) ASIO error detail: {asioError} (Code: {(int)asioError})", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Sets the output audio device to an ASIO device by its index.
        /// </summary>
        /// <param name="asioDeviceIndex">The index of the ASIO device to use.</param>
        /// <returns>True if the device was successfully set, false otherwise.</returns>
        private bool setAudioDeviceAsio(int asioDeviceIndex)
        {
            if (isAsioActive && GlobalMixerHandle.Value != null)
            {
                if (!asioDuplicateLogged)
                {
                    Logger.Log($"[AudioDebug] (AudioManager) Skip setAudioDeviceAsio duplicate index={asioDeviceIndex} globalMixer={GlobalMixerHandle.Value}", LoggingTarget.Runtime, LogLevel.Debug);
                    asioDuplicateLogged = true;
                }
                return true;
            }
            // Only free non-ASIO devices, ASIO devices are handled separately
            var currentDevice = audioDevices.ElementAtOrDefault(Bass.CurrentDevice);
            if (currentDevice.Driver?.StartsWith("asio:", StringComparison.Ordinal) != true)
            {
                // Free any existing non-ASIO device
                freeDevice(Bass.CurrentDevice);
            }

            // Initialize ASIO device through the audio thread
            bool initSuccess = InitAsio(asioDeviceIndex);
            if (!initSuccess)
            {
                var st = new StackTrace(1, true);
                Logger.Log($"ASIO failed to initialize (index={asioDeviceIndex}). Stack:\n{st}", level: LogLevel.Error);
                return false;
            }

            var asioDevices = AsioDeviceManager.AvailableDevices.ToList();
            if (asioDeviceIndex >= 0 && asioDeviceIndex < asioDevices.Count)
            {
                Logger.Log($@"🔈 ASIO initialised
                              Device:                 {asioDevices[asioDeviceIndex].Name}
                              Driver:                 {asioDevices[asioDeviceIndex].Driver}");
            }
            // 初始化成功后刷新所有Mixer：但 AudioThread.initAsio 已经调用过 RecreateMixers -> createMixer。
            // 避免第二次 RecreateMixers 导致重复 createMixer 和多余 mixer 句柄。
            bool needDeviceUpdate = false;
            if (!asioMixersInitialised) // 还没标记初始化，通过一次 UpdateDevice(-1) 完成最后绑定。
                needDeviceUpdate = true;
            else
            {
                // 如果 Track/Sample mixer 仍未创建（句柄为0），再做一次补充初始化。
                if (TrackMixer is Mixing.Bass.BassAudioMixer t && t.Handle == 0) needDeviceUpdate = true;
                if (SampleMixer is Mixing.Bass.BassAudioMixer s && s.Handle == 0) needDeviceUpdate = true;
            }
            if (needDeviceUpdate)
            {
                Logger.Log($"[AudioDebug] (AudioManager) Performing UpdateDevice(-1) needDeviceUpdate={needDeviceUpdate} asioMixersInitialised={asioMixersInitialised}", LoggingTarget.Runtime, LogLevel.Debug);
                UpdateDevice(-1); // -1 代表ASIO设备或特殊设备
            }
            else
            {
                Logger.Log("[AudioDebug] (AudioManager) Skip redundant UpdateDevice(-1) (mixers already created)", LoggingTarget.Runtime, LogLevel.Debug);
            }

            return true;
        }

        /// <summary>
        /// Sets the output audio device to a WASAPI device by its name.
        /// </summary>
        /// <param name="deviceName">The name of the WASAPI device to use.</param>
        /// <param name="exclusive">Whether to use exclusive mode (true) or shared mode (false).</param>
        /// <returns>True if the device was successfully set, false otherwise.</returns>
        private bool setAudioDeviceWasapi(string deviceName, bool exclusive)
        {
            // 防止重复初始化相同的WASAPI设备
            if (exclusive && wasapiExclusiveInitialised && GlobalMixerHandle.Value != null)
            {
                Logger.Log($"[AudioDebug] (AudioManager) WASAPI Exclusive already initialized for device: {deviceName}, skipping", LoggingTarget.Runtime, LogLevel.Debug);
                return true;
            }

            if (!exclusive && !wasapiExclusiveInitialised && GlobalMixerHandle.Value != null)
            {
                Logger.Log($"[AudioDebug] (AudioManager) WASAPI Shared already initialized for device: {deviceName}, skipping", LoggingTarget.Runtime, LogLevel.Debug);
                return true;
            }

            // Find the device index by name
            int deviceIndex = audioDeviceNames.FindIndex(d => d == deviceName);
            if (deviceIndex < 0)
            {
                Logger.Log($"WASAPI device not found: {deviceName}", level: LogLevel.Error);
                wasapiExclusiveInitialised = false;
                return false;
            }

            // 确保完全释放现有设备，特别是对于独占模式
            Logger.Log($"[AudioDebug] (AudioManager) Freeing current device before WASAPI {(exclusive ? "Exclusive" : "Shared")} init", LoggingTarget.Runtime, LogLevel.Debug);
            freeDevice(Bass.CurrentDevice);

            // 额外等待以确保设备完全释放（独占模式特别需要）
            if (exclusive)
            {
                System.Threading.Thread.Sleep(50); // 短暂等待确保设备释放
            }

            // Initialize the device with WASAPI flags
            int bassDeviceIndex = BASS_INTERNAL_DEVICE_COUNT + deviceIndex;

            if (exclusive)
            {
                int wasapiDeviceIndex = findWasapiDeviceIndex(bassDeviceIndex);
                if (wasapiDeviceIndex >= 0)
                {
                    Logger.Log($"[AudioDebug] (AudioManager) Initialising WASAPI Exclusive: device={deviceName}, wasapiIndex={wasapiDeviceIndex}", LoggingTarget.Runtime, LogLevel.Debug);

                    // 直接调用 AudioThread 的 wasapi 初始化，指定首选独占模式
                    thread.initWasapi(wasapiDeviceIndex, preferExclusive: true);

                    if (GlobalMixerHandle.Value == null)
                    {
                        Logger.Log($"[AudioDebug] (AudioManager) WASAPI Exclusive init failed: global mixer not created.", LoggingTarget.Runtime, LogLevel.Error);
                        wasapiExclusiveInitialised = false;
                        return false;
                    }

                    Logger.Log($@"🔈 WASAPI Exclusive initialised
                                  Device:                 {deviceName}
                                  Mode:                   Exclusive
                                  GlobalMixerHandle:      {GlobalMixerHandle.Value}");

                    // 重建并强制创建 Track/Sample mixer（decode 模式）
                    RecreateMixers();
                    int current = Bass.CurrentDevice; // 可能仍为 -1，在 decode 模式下允许
                    if (TrackMixer is Mixing.Bass.BassAudioMixer tm && tm.Handle == 0)
                        tm.UpdateDevice(current);
                    if (SampleMixer is Mixing.Bass.BassAudioMixer sm && sm.Handle == 0)
                        sm.UpdateDevice(current);
                    Logger.Log($"[AudioDebug] (AudioManager) WASAPI Exclusive mixers forced creation. CurrentDevice={Bass.CurrentDevice}, TrackHandle={(TrackMixer as Mixing.Bass.BassAudioMixer)?.Handle}, SampleHandle={(SampleMixer as Mixing.Bass.BassAudioMixer)?.Handle}", LoggingTarget.Runtime, LogLevel.Debug);

                    wasapiExclusiveInitialised = true;
                    return true;
                }
                else
                {
                    wasapiExclusiveInitialised = false;
                    Logger.Log($"WASAPI Exclusive device not found for BASS device index: {bassDeviceIndex}", level: LogLevel.Error);
                    return false;
                }
            }
            else
            {
                wasapiExclusiveInitialised = false;

                // WASAPI共享模式也应该通过AudioThread.initWasapi创建全局混音器
                // 这确保了共享模式和独占模式都有相同的混音器架构
                int wasapiDeviceIndex = findWasapiDeviceIndex(bassDeviceIndex);
                if (wasapiDeviceIndex >= 0)
                {
                    Logger.Log($"[AudioDebug] (AudioManager) Initialising WASAPI Shared: device={deviceName}, wasapiIndex={wasapiDeviceIndex}", LoggingTarget.Runtime, LogLevel.Debug);

                    // 直接调用 AudioThread 的 wasapi 初始化，指定首选共享模式
                    thread.initWasapi(wasapiDeviceIndex, preferExclusive: false);

                    if (GlobalMixerHandle.Value == null)
                    {
                        Logger.Log($"[AudioDebug] (AudioManager) WASAPI Shared init failed: global mixer not created.", LoggingTarget.Runtime, LogLevel.Error);
                        return false;
                    }

                    Logger.Log($@"🔈 WASAPI Shared initialised
                                  Device:                 {deviceName}
                                  Mode:                   Shared
                                  GlobalMixerHandle:      {GlobalMixerHandle.Value}");

                    // 重建并强制创建 Track/Sample mixer（decode 模式）
                    RecreateMixers();
                    int current = Bass.CurrentDevice; // 可能仍为 -1，在 decode 模式下允许
                    if (TrackMixer is Mixing.Bass.BassAudioMixer tm && tm.Handle == 0)
                        tm.UpdateDevice(current);
                    if (SampleMixer is Mixing.Bass.BassAudioMixer sm && sm.Handle == 0)
                        sm.UpdateDevice(current);
                    Logger.Log($"[AudioDebug] (AudioManager) WASAPI Shared mixers forced creation. CurrentDevice={Bass.CurrentDevice}, TrackHandle={(TrackMixer as Mixing.Bass.BassAudioMixer)?.Handle}, SampleHandle={(SampleMixer as Mixing.Bass.BassAudioMixer)?.Handle}", LoggingTarget.Runtime, LogLevel.Debug);

                    return true;
                }
                else
                {
                    Logger.Log($"WASAPI Shared device not found for BASS device index: {bassDeviceIndex}", level: LogLevel.Error);
                    return false;
                }
            }
        }

        /// <summary>
        /// Finds the WASAPI device index corresponding to a BASS device index.
        /// </summary>
        /// <param name="bassDeviceIndex">The BASS device index.</param>
        /// <returns>The WASAPI device index, or -1 if not found.</returns>
        private int findWasapiDeviceIndex(int bassDeviceIndex)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return -1;

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (bassDeviceIndex > 0)
            {
                string driver = Bass.GetDeviceInfo(bassDeviceIndex).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
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
                            return wasapiDevice;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Initializes BASS with WASAPI-specific configuration.
        /// </summary>
        /// <param name="device">The device index to initialize.</param>
        /// <param name="exclusive">Whether to use exclusive mode.</param>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        protected virtual bool InitBassWASAPI(int device, bool exclusive)
        {
            if (Bass.CurrentDevice == device)
                return true;

            // Set standard BASS configuration
            Bass.UpdatePeriod = 5;
            Bass.DeviceBufferLength = (int)AudioDeviceBufferLength.Value;
            Bass.PlaybackBufferLength = 100;
            Bass.DeviceNonStop = true;
            Bass.Configure(ManagedBass.Configuration.TruePlayPosition, 0);
            Bass.Configure(ManagedBass.Configuration.IOSSession, 16);
            Bass.Configure(ManagedBass.Configuration.IncludeDefaultDevice, true);
            Bass.Configure((ManagedBass.Configuration)68, 1);
            Bass.Configure((ManagedBass.Configuration)70, false);

            if (!thread.InitDevice(device))
                return false;

            return true;
        }

        /// <summary>
        /// Frees the currently initialized device.
        /// </summary>
        /// <param name="deviceIndex">The index of the device to free.</param>
        private void freeDevice(int deviceIndex)
        {
            var device = audioDevices.ElementAtOrDefault(deviceIndex);

            // Check if this is an ASIO device
            if (device.Driver?.StartsWith("asio:", StringComparison.Ordinal) == true)
            {
                AsioDeviceManager.FreeDevice();
            }
            else
            {
                // Free regular BASS device
                thread.FreeDevice(deviceIndex);
            }
        }


        private void syncAudioDevices()
        {
            audioDevices = GetAllDevices();

            // Bass should always be providing "No sound" and "Default" device.
            Trace.Assert(audioDevices.Length >= BASS_INTERNAL_DEVICE_COUNT, "Bass did not provide any audio devices.");

            var oldDeviceNames = audioDeviceNames;
            var newDeviceNames = audioDeviceNames = audioDevices.Skip(BASS_INTERNAL_DEVICE_COUNT).Where(d => d.IsEnabled).Select(d => d.Name).ToImmutableList();

            onDevicesChanged();

            var newDevices = newDeviceNames.Except(oldDeviceNames).ToList();
            var lostDevices = oldDeviceNames.Except(newDeviceNames).ToList();

            if (newDevices.Count > 0 || lostDevices.Count > 0)
            {
                eventScheduler.Add(delegate
                {
                    foreach (string d in newDevices)
                        OnNewDevice?.Invoke(d);
                    foreach (string d in lostDevices)
                        OnLostDevice?.Invoke(d);
                });
            }
        }

        /// <summary>
        /// Check whether any audio device changes have occurred.
        ///
        /// Changes supported are:
        /// - A new device is added
        /// - An existing device is Enabled/Disabled or set as Default
        /// </summary>
        /// <remarks>
        /// This method is optimised to incur the lowest overhead possible.
        /// </remarks>
        /// <param name="previousDevices">The previous audio devices array.</param>
        /// <returns>Whether a change was detected.</returns>
        protected virtual bool CheckForDeviceChanges(ImmutableArray<DeviceInfo> previousDevices)
        {
            int deviceCount = Bass.DeviceCount;

            if (previousDevices.Length != deviceCount)
                return true;

            for (int i = 0; i < deviceCount; i++)
            {
                var prevInfo = previousDevices[i];

                Bass.GetDeviceInfo(i, out var info);

                if (info.IsEnabled != prevInfo.IsEnabled)
                    return true;

                if (info.IsDefault != prevInfo.IsDefault)
                    return true;
            }

            return false;
        }

        protected virtual ImmutableArray<DeviceInfo> GetAllDevices()
        {
            int deviceCount = Bass.DeviceCount;

            var devices = ImmutableArray.CreateBuilder<DeviceInfo>(deviceCount);
            for (int i = 0; i < deviceCount; i++)
                devices.Add(Bass.GetDeviceInfo(i));

            // Add ASIO devices to the device list by creating a custom device info structure
            var asioDevices = AsioDeviceManager.AvailableDevices.ToList();
            for (int i = 0; i < asioDevices.Count; i++)
            {
                var asioDevice = asioDevices[i];
                Bass.GetDeviceInfo(0, out _); // template fetch ignored
                devices.Add(createAsioDeviceInfo(asioDevice.Name, i));
            }

            // Create a new builder with the correct capacity to avoid MoveToImmutable error
            var finalDevices = ImmutableArray.CreateBuilder<DeviceInfo>(devices.Count);
            for (int i = 0; i < devices.Count; i++)
                finalDevices.Add(devices[i]);

            return finalDevices.MoveToImmutable();
        }

        /// <summary>
        /// Creates a custom DeviceInfo structure for ASIO devices.
        /// </summary>
        /// <param name="name">The name of the ASIO device.</param>
        /// <param name="index">The index of the ASIO device.</param>
        /// <returns>A DeviceInfo structure representing the ASIO device.</returns>
        private DeviceInfo createAsioDeviceInfo(string name, int index)
        {
            Bass.GetDeviceInfo(0, out var deviceInfo);
            return deviceInfo;
        }

        // The current device is considered valid if it is enabled, initialized, and not a fallback device.
        protected virtual bool IsCurrentDeviceValid()
        {
            if (wasapiExclusiveInitialised)
                return true;
            var device = audioDevices.ElementAtOrDefault(Bass.CurrentDevice);
            bool isFallback = string.IsNullOrEmpty(AudioDevice.Value) ? !device.IsDefault : device.Name != AudioDevice.Value;
            return device.IsEnabled && device.IsInitialized && !isFallback;
        }

        public override string ToString()
        {
            string deviceName = audioDevices.ElementAtOrDefault(Bass.CurrentDevice).Name;
            return $@"{GetType().ReadableName()} ({deviceName ?? "Unknown"})";
        }
    }
}

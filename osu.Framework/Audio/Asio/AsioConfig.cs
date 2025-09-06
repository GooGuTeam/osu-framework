// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;

namespace osu.Framework.Audio.Asio
{
    /// <summary>
    /// Configuration class for ASIO audio settings.
    /// </summary>
    public class AsioConfig
    {
        /// <summary>
        /// Gets a bindable for the ASIO device index.
        /// </summary>
        public Bindable<int> DeviceIndex { get; } = new Bindable<int>(-1); // -1 means auto-select

        /// <summary>
        /// Gets a bindable for the ASIO sample rate.
        /// </summary>
        public Bindable<double> SampleRate { get; } = new Bindable<double>(48000.0); // Default sample rate

        /// <summary>
        /// Gets a bindable for the ASIO buffer size in seconds.
        /// </summary>
        public Bindable<double> BufferSize { get; } = new Bindable<double>(0.0); // 0 means use device default

        /// <summary>
        /// Gets a bindable for the ASIO channel count.
        /// </summary>
        public Bindable<int> ChannelCount { get; } = new Bindable<int>(2); // Stereo by default

        /// <summary>
        /// Gets a bindable indicating whether ASIO is enabled.
        /// </summary>
        public Bindable<bool> Enabled { get; } = new Bindable<bool>(false); // ASIO disabled by default
    }
}

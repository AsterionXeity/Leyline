using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Leyline.Models;

namespace Leyline.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = ChannelStripProcessor.EngineSampleRate;
    public static readonly WaveFormat Format = ChannelStripProcessor.EngineWaveFormat;

    private readonly object _engineLock = new();
    // Shared-mode output buffer for the monitor, Stream Mix and Mic Rec outputs. Measured Browser cable -> Stream Mix
    // cable, 10, 15 and 20 ms gave the same end-to-end latency (Windows mixes in 10 ms periods), so this keeps the
    // most headroom for effect chains. LowLatencyBufferReader keeps the per-mix buffers in front of it near empty.
    private const int OutputLatencyMs = 20;

    private readonly List<ChannelStripProcessor> _channels = new();

    private readonly MixingSampleProvider _personalMixer;
    private readonly MixingSampleProvider _streamMixer;
    private readonly MixingSampleProvider _discordMixer;
    private readonly MixingSampleProvider _micRecMixer;

    private readonly MasterBusSampleProvider _personalMasterProvider;
    private readonly MasterBusSampleProvider _streamMasterProvider;
    private readonly MasterBusSampleProvider _discordMasterProvider;
    private readonly MasterBusSampleProvider _micRecMasterProvider;

    private WasapiOut? _monitorOut;
    private WasapiOut? _streamOut;
    private WasapiOut? _micRecOut;

    // The monitor's copy of the mix it plays, when that mix already has its own output (see GetMonitorSource)
    private (MasterBusSampleProvider Mix, BufferedWaveProvider Tap)? _monitorTap;

    // Extra devices each mix also plays to (index as _listeningMixIndex), and the outputs playing them
    private readonly List<string>[] _extraOutputIds = [new(), new(), new(), new()];
    private readonly List<ExtraOutput> _extraOutputs = new();

    private sealed record ExtraOutput(int Mix, string DeviceId, WasapiOut Output, BufferedWaveProvider? Tap);
    private string _currentMonitorDeviceId = string.Empty;
    private string _currentStreamDeviceId = string.Empty;
    private string _requestedStreamDeviceId = string.Empty;  // user's Stream output choice ("" = none)
    private string _streamCableDeviceId = string.Empty;      // Leyline "Stream Mix" cable: default Stream output
    private string _currentMicRecDeviceId = string.Empty;    // Leyline "Microphone" cable
    private int _listeningMixIndex; // 0: Personal Mix, 1: Stream Mix, 2: Discord Record Mix, 3: Mic Rec Mix

    private WaveFileWriter? _streamRecorder;
    private readonly object _recordLock = new();

    public DspChainProcessor MonitorMasterDsp { get; }
    public DspChainProcessor StreamMasterDsp { get; }

    public DspChainModel MonitorMasterDspModel { get; set; } = new();
    public DspChainModel StreamMasterDspModel { get; set; } = new();

    public float MonitorMasterVolume { get; set; } = 0.95f;
    public float StreamMasterVolume { get; set; } = 0.95f;
    public bool IsMonitorMasterMuted { get; set; }
    public bool IsStreamMasterMuted { get; set; }

    public float MonitorPeakLeft => _personalMasterProvider.SmoothedPeakLeft;
    public float MonitorPeakRight => _personalMasterProvider.SmoothedPeakRight;
    public float StreamPeakLeft => _streamMasterProvider.SmoothedPeakLeft;
    public float StreamPeakRight => _streamMasterProvider.SmoothedPeakRight;

    public bool IsRecordingStream { get; private set; }
    public string ActiveRecordingPath { get; private set; } = string.Empty;

    public AudioEngine()
    {
        MonitorMasterDsp = new DspChainProcessor(SampleRate);
        StreamMasterDsp = new DspChainProcessor(SampleRate);

        _personalMixer = new MixingSampleProvider(Format) { ReadFully = true };
        _streamMixer = new MixingSampleProvider(Format) { ReadFully = true };
        _discordMixer = new MixingSampleProvider(Format) { ReadFully = true };
        _micRecMixer = new MixingSampleProvider(Format) { ReadFully = true };

        _personalMasterProvider = new MasterBusSampleProvider(
            _personalMixer,
            MonitorMasterDsp,
            () => MonitorMasterDspModel,
            () => IsMonitorMasterMuted ? 0f : MonitorMasterVolume,
            null);

        _streamMasterProvider = new MasterBusSampleProvider(
            _streamMixer,
            StreamMasterDsp,
            () => StreamMasterDspModel,
            () => IsStreamMasterMuted ? 0f : StreamMasterVolume,
            WriteRecordedStreamSamples);

        _discordMasterProvider = new MasterBusSampleProvider(
            _discordMixer,
            new DspChainProcessor(SampleRate),
            () => new DspChainModel(),
            () => 1.0f,
            null);

        _micRecMasterProvider = new MasterBusSampleProvider(
            _micRecMixer,
            new DspChainProcessor(SampleRate),
            () => new DspChainModel(),
            () => 1.0f,
            null);
    }

    /// <summary>
    /// Enumerates audio endpoints while strictly excluding Elgato Virtual Audio drivers.
    /// </summary>
    public static List<AudioDeviceInfo> EnumerateDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultCaptureId = string.Empty;
            string defaultRenderId = string.Empty;

            try { defaultCaptureId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }
            try { defaultRenderId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                // Strictly exclude Elgato drivers as requested
                if (dev.FriendlyName.Contains("Elgato", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isVirt = IsVirtualEndpointName(dev.FriendlyName);
                list.Add(new AudioDeviceInfo
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    Kind = AudioEndpointKind.CaptureInput,
                    IsDefault = dev.ID == defaultCaptureId,
                    IsVirtual = isVirt
                });
            }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                // Strictly exclude Elgato drivers as requested
                if (dev.FriendlyName.Contains("Elgato", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isVirt = IsVirtualEndpointName(dev.FriendlyName);
                list.Add(new AudioDeviceInfo
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    Kind = isVirt ? AudioEndpointKind.VirtualEndpoint : AudioEndpointKind.RenderOutput,
                    IsDefault = dev.ID == defaultRenderId,
                    IsVirtual = isVirt
                });
            }
        }
        catch
        {
        }
        return list;
    }

    public static bool IsVirtualEndpointName(string friendlyName)
    {
        return friendlyName.Contains("Leyline", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);
    }

    public void SetListeningMixIndex(int mixIndex)
    {
        lock (_engineLock)
        {
            _listeningMixIndex = Math.Clamp(mixIndex, 0, 3);
            SetMonitorOutputDevice(_currentMonitorDeviceId);
        }
    }

    /// <summary>
    /// Connects the user's selected Monitoring Output Device STRICTLY to the selected Listening Mix.
    /// If deviceId is empty ("None / Unselected"), monitoring is completely turned off (never auto-selects a default device).
    /// </summary>
    public void SetMonitorOutputDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            string previousId = _currentMonitorDeviceId;
            _currentMonitorDeviceId = deviceId ?? string.Empty;
            RebuildMonitorAndExtraOutputs();
            RestartChannelsOnDevices(previousId, _currentMonitorDeviceId);
        }
    }

    private void ApplyMonitorOutputDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _monitorOut = null;
            if (_monitorTap is { } previousTap)
                previousTap.Mix.RemoveTap(previousTap.Tap);
            _monitorTap = null;
            _currentMonitorDeviceId = deviceId ?? string.Empty;

            // If user selected "None (Unselected)", keep monitoring completely off
            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var target = enumerator.GetDevice(deviceId);
                if (target == null || target.State != DeviceState.Active || target.DataFlow != DataFlow.Render)
                {
                    _currentMonitorDeviceId = string.Empty;
                    return;
                }

                _monitorOut = new WasapiOut(target, AudioClientShareMode.Shared, true, OutputLatencyMs);
                _monitorOut.Init(GetMonitorSource(_listeningMixIndex));
                _monitorOut.Play();
            }
            catch
            {
                _currentMonitorDeviceId = string.Empty;
            }
        }
    }

    /// <summary>
    /// The user's Stream output choice. Empty ("None") sends the Stream Mix to Leyline's "Stream Mix" cable
    /// (see SetStreamMixCableDevice) so OBS can record it without any setup.
    /// </summary>
    public void SetStreamOutputDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            _requestedStreamDeviceId = deviceId ?? string.Empty;
            ApplyEffectiveStreamOutput();
        }
    }

    /// <summary>Playback endpoint of Leyline's "Stream Mix" cable (empty when the driver is not running).</summary>
    public void SetStreamMixCableDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            _streamCableDeviceId = deviceId ?? string.Empty;
            ApplyEffectiveStreamOutput();
        }
    }

    private void ApplyEffectiveStreamOutput()
    {
        string effective = string.IsNullOrWhiteSpace(_requestedStreamDeviceId) ? _streamCableDeviceId : _requestedStreamDeviceId;
        if (effective.Equals(_currentStreamDeviceId, StringComparison.OrdinalIgnoreCase) && (_streamOut != null || string.IsNullOrWhiteSpace(effective)))
            return;

        string previousId = _currentStreamDeviceId;
        ApplyStreamOutputDevice(effective);
        RestartChannelsOnDevices(previousId, _currentStreamDeviceId);
        RefreshMonitorSourceIfListeningTo(1);
    }

    /// <summary>
    /// Mic Rec mix output: Leyline's "Microphone" cable, which voice apps use as their microphone.
    /// Empty stops the output.
    /// </summary>
    public void SetMicRecOutputDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            string previousId = _currentMicRecDeviceId;
            _micRecOut?.Stop();
            _micRecOut?.Dispose();
            _micRecOut = null;
            _currentMicRecDeviceId = deviceId ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var target = enumerator.GetDevice(deviceId);
                    if (target != null && target.State == DeviceState.Active && target.DataFlow == DataFlow.Render)
                    {
                        _micRecOut = new WasapiOut(target, AudioClientShareMode.Shared, true, OutputLatencyMs);
                        _micRecOut.Init(_micRecMasterProvider);
                        _micRecOut.Play();
                    }
                }
                catch
                {
                    _micRecOut = null;
                }
            }

            RestartChannelsOnDevices(previousId, _currentMicRecDeviceId);
            RefreshMonitorSourceIfListeningTo(3);
        }
    }

    /// <summary>
    /// What the monitor plays when listening to a mix. A mix that already has its own output (Stream Mix to its
    /// cable or device, Mic Rec to the Microphone cable) is read by that output, so the monitor listens to a copy
    /// (tap) instead; two readers on one mix would each get every other block.
    /// </summary>
    private ISampleProvider GetMonitorSource(int mixIndex)
    {
        var mix = MixProvider(mixIndex);
        if (!HasOwnOutput(mixIndex))
            return mix;

        var (reader, tap) = mix.AddTap();
        _monitorTap = (mix, tap);
        return reader;
    }

    private MasterBusSampleProvider MixProvider(int mixIndex) => mixIndex switch
    {
        1 => _streamMasterProvider,
        2 => _discordMasterProvider,
        3 => _micRecMasterProvider,
        _ => _personalMasterProvider
    };

    // Stream Mix (its cable or device) and Mic Rec (the Microphone cable) are read by their own outputs
    private bool HasOwnOutput(int mixIndex) =>
        (mixIndex == 1 && _streamOut != null) || (mixIndex == 3 && _micRecOut != null);

    // A mix's own output changed: the monitor and extra outputs on it switch between reading it and a copy
    private void RefreshMonitorSourceIfListeningTo(int mixIndex)
    {
        if (_listeningMixIndex == mixIndex || _extraOutputIds[mixIndex].Count > 0)
            RebuildMonitorAndExtraOutputs();
    }

    /// <summary>
    /// Extra devices a mix also plays to, besides the monitor (when listening to it) and the mix's own output, such as
    /// speakers next to the headphones. Leyline's own cables and the monitor device itself are not allowed by the UI.
    /// </summary>
    public void SetMixExtraOutputs(int mixIndex, IEnumerable<string> deviceIds)
    {
        if (mixIndex < 0 || mixIndex >= _extraOutputIds.Length)
            return;

        lock (_engineLock)
        {
            var previous = _extraOutputIds[mixIndex];
            _extraOutputIds[mixIndex] = deviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            RebuildMonitorAndExtraOutputs();

            foreach (string id in previous.Union(_extraOutputIds[mixIndex], StringComparer.OrdinalIgnoreCase))
                RestartChannelsOnDevices(string.Empty, id);
        }
    }

    /// <summary>Devices a mix is playing to right now (monitor, its own output and extra outputs).</summary>
    public int GetMixOutputCount(int mixIndex)
    {
        lock (_engineLock)
        {
            int count = _extraOutputs.Count(e => e.Mix == mixIndex);
            if (_monitorOut != null && _listeningMixIndex == mixIndex)
                count++;
            if (HasOwnOutput(mixIndex))
                count++;
            return count;
        }
    }

    // Each mix is read by one output; any other output on it plays a copy (two readers would each get every other
    // block). Which output reads the mix depends on the monitor, the mix's own output and its extra outputs, so the
    // monitor and all extra outputs are rebuilt together whenever one of them changes.
    private void RebuildMonitorAndExtraOutputs()
    {
        foreach (var extra in _extraOutputs)
        {
            extra.Output.Stop();
            extra.Output.Dispose();
            if (extra.Tap != null)
                MixProvider(extra.Mix).RemoveTap(extra.Tap);
        }
        _extraOutputs.Clear();

        ApplyMonitorOutputDevice(_currentMonitorDeviceId);

        using var enumerator = new MMDeviceEnumerator();
        for (int mixIndex = 0; mixIndex < _extraOutputIds.Length; mixIndex++)
        {
            var mix = MixProvider(mixIndex);
            bool mixIsRead = HasOwnOutput(mixIndex) || (_monitorOut != null && _listeningMixIndex == mixIndex);

            foreach (string deviceId in _extraOutputIds[mixIndex])
            {
                if (_monitorOut != null && _listeningMixIndex == mixIndex &&
                    deviceId.Equals(_currentMonitorDeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;   // already playing this mix there

                BufferedWaveProvider? tap = null;
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device == null || device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                        continue;

                    ISampleProvider source = mix;
                    if (mixIsRead)
                        (source, tap) = mix.AddTap();

                    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
                    output.Init(source);
                    output.Play();
                    _extraOutputs.Add(new ExtraOutput(mixIndex, deviceId, output, tap));
                    mixIsRead = true;
                }
                catch
                {
                    if (tap != null)
                        mix.RemoveTap(tap);
                }
            }
        }
    }

    private void ApplyStreamOutputDevice(string? deviceId)
    {
        lock (_engineLock)
        {
            _streamOut?.Stop();
            _streamOut?.Dispose();
            _streamOut = null;
            _currentStreamDeviceId = deviceId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            // Never let StreamOut play to the same physical headphones as MonitorOut (would cause Stream Mix bleed into headphones!)
            if (!string.IsNullOrWhiteSpace(_currentMonitorDeviceId) &&
                deviceId.Equals(_currentMonitorDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var target = enumerator.GetDevice(deviceId);
                if (target != null && target.State == DeviceState.Active && target.DataFlow == DataFlow.Render)
                {
                    _streamOut = new WasapiOut(target, AudioClientShareMode.Shared, true, OutputLatencyMs);
                    _streamOut.Init(_streamMasterProvider);
                    _streamOut.Play();
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// True when a mix is currently playing to this device. Channels must never loopback-capture it,
    /// or the mix would be fed back into itself (doubled audio / echo).
    /// </summary>
    private bool IsMixOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        return deviceId.Equals(_currentMonitorDeviceId, StringComparison.OrdinalIgnoreCase) ||
               deviceId.Equals(_currentStreamDeviceId, StringComparison.OrdinalIgnoreCase) ||
               deviceId.Equals(_currentMicRecDeviceId, StringComparison.OrdinalIgnoreCase) ||
               _extraOutputIds.Any(ids => ids.Contains(deviceId, StringComparer.OrdinalIgnoreCase));
    }

    // Restart channels bound to a device whose mix-output status changed, so the loopback guard re-evaluates.
    private void RestartChannelsOnDevices(string previousId, string newId)
    {
        if (string.Equals(previousId, newId, StringComparison.OrdinalIgnoreCase))
            return;

        var affected = _channels
            .Select(c => c.Model)
            .Where(m => !string.IsNullOrWhiteSpace(m.DeviceId) &&
                        (m.DeviceId.Equals(previousId, StringComparison.OrdinalIgnoreCase) ||
                         m.DeviceId.Equals(newId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var model in affected)
            AddOrRestartChannel(model);
    }

    public ChannelStripProcessor AddOrRestartChannel(ChannelStripModel model)
    {
        lock (_engineLock)
        {
            RemoveChannel(model.Id);
            var proc = new ChannelStripProcessor(model, IsMixOutputDevice);
            _channels.Add(proc);
            _personalMixer.AddMixerInput(proc.MonitorOutput);
            _streamMixer.AddMixerInput(proc.StreamOutput);
            _discordMixer.AddMixerInput(proc.DiscordOutput);
            _micRecMixer.AddMixerInput(proc.MicRecOutput);
            proc.Start();
            return proc;
        }
    }

    public ChannelStripProcessor? GetChannelProcessor(string channelId)
    {
        lock (_engineLock)
        {
            return _channels.FirstOrDefault(c => c.Model.Id == channelId);
        }
    }

    public void RemoveChannel(string channelId)
    {
        lock (_engineLock)
        {
            var existing = _channels.FirstOrDefault(c => c.Model.Id == channelId);
            if (existing != null)
            {
                _personalMixer.RemoveMixerInput(existing.MonitorOutput);
                _streamMixer.RemoveMixerInput(existing.StreamOutput);
                _discordMixer.RemoveMixerInput(existing.DiscordOutput);
                _micRecMixer.RemoveMixerInput(existing.MicRecOutput);
                existing.Dispose();
                _channels.Remove(existing);
            }
        }
    }

    /// <summary>
    /// Decays and publishes all meters; <paramref name="elapsedMs"/> since the last call keeps the fall-off speed
    /// the same at any UI frame rate (the decay factors were tuned for 30 updates per second).
    /// </summary>
    public void UpdateAllMeters(double elapsedMs)
    {
        double ticks = elapsedMs / 33.3;
        float channelDecay = (float)Math.Pow(0.84, ticks);
        float masterDecay = (float)Math.Pow(0.88, ticks);

        lock (_engineLock)
        {
            foreach (var ch in _channels)
            {
                ch.DecayAndPublishMeters(channelDecay);
            }
            _personalMasterProvider.DecayMeters(masterDecay);
            _streamMasterProvider.DecayMeters(masterDecay);
            _discordMasterProvider.DecayMeters(masterDecay);
            _micRecMasterProvider.DecayMeters(masterDecay);
        }
    }

    public string ToggleStreamRecording()
    {
        lock (_recordLock)
        {
            if (IsRecordingStream && _streamRecorder != null)
            {
                _streamRecorder.Dispose();
                _streamRecorder = null;
                IsRecordingStream = false;
                return ActiveRecordingPath;
            }

            string recDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Leyline Recordings");
            Directory.CreateDirectory(recDir);

            string filePath = Path.Combine(recDir, $"Leyline_StreamMix_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            _streamRecorder = new WaveFileWriter(filePath, Format);
            ActiveRecordingPath = filePath;
            IsRecordingStream = true;
            return filePath;
        }
    }

    private void WriteRecordedStreamSamples(float[] buffer, int offset, int count)
    {
        if (!IsRecordingStream) return;
        lock (_recordLock)
        {
            _streamRecorder?.WriteSamples(buffer, offset, count);
        }
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _streamOut?.Stop();
            _streamOut?.Dispose();
            _micRecOut?.Stop();
            _micRecOut?.Dispose();
            foreach (var extra in _extraOutputs)
            {
                extra.Output.Stop();
                extra.Output.Dispose();
            }
            _extraOutputs.Clear();

            foreach (var ch in _channels)
                ch.Dispose();
            _channels.Clear();

            MonitorMasterDsp.Dispose();
            StreamMasterDsp.Dispose();
        }

        lock (_recordLock)
        {
            _streamRecorder?.Dispose();
            _streamRecorder = null;
        }
    }

    private sealed class MasterBusSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly DspChainProcessor _dsp;
        private readonly Func<DspChainModel> _dspModelGetter;
        private readonly Func<float> _masterVolumeGetter;
        private readonly Action<float[], int, int>? _tapCallback;
        private BufferedWaveProvider[] _taps = [];   // replaced, never changed in place: Read runs on an output thread
        private readonly object _tapLock = new();
        private byte[] _tapBytes = [];

        public float SmoothedPeakLeft { get; private set; }
        public float SmoothedPeakRight { get; private set; }

        public WaveFormat WaveFormat => Format;

        public MasterBusSampleProvider(
            ISampleProvider source,
            DspChainProcessor dsp,
            Func<DspChainModel> dspModelGetter,
            Func<float> masterVolumeGetter,
            Action<float[], int, int>? tapCallback)
        {
            _source = source;
            _dsp = dsp;
            _dspModelGetter = dspModelGetter;
            _masterVolumeGetter = masterVolumeGetter;
            _tapCallback = tapCallback;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int frames = read / 2;
            if (frames <= 0) return read;

            float vol = _masterVolumeGetter();
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] *= vol;
            }

            Span<float> span = buffer.AsSpan(offset, frames * 2);
            _dsp.ProcessStereoInterleaved(
                span,
                frames,
                0f,
                _dspModelGetter(),
                null,
                out float pL,
                out float pR);

            SmoothedPeakLeft = Math.Max(pL, SmoothedPeakLeft * 0.85f);
            SmoothedPeakRight = Math.Max(pR, SmoothedPeakRight * 0.85f);

            _tapCallback?.Invoke(buffer, offset, read);

            var taps = _taps;
            if (taps.Length > 0)
            {
                int bytes = read * sizeof(float);
                if (_tapBytes.Length < bytes)
                    _tapBytes = new byte[bytes];
                Buffer.BlockCopy(buffer, offset * sizeof(float), _tapBytes, 0, bytes);
                foreach (var tap in taps)
                    tap.AddSamples(_tapBytes, 0, bytes);
            }

            return read;
        }

        /// <summary>
        /// A copy of this mix for another output to play while one output reads the mix itself. Every block this mix
        /// produces is copied into the tap until <see cref="RemoveTap"/>.
        /// </summary>
        public (ISampleProvider Reader, BufferedWaveProvider Tap) AddTap()
        {
            var tap = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            lock (_tapLock)
                _taps = [.. _taps, tap];
            return (new LowLatencyBufferReader(tap), tap);
        }

        public void RemoveTap(BufferedWaveProvider tap)
        {
            lock (_tapLock)
                _taps = _taps.Where(t => !ReferenceEquals(t, tap)).ToArray();
        }

        public void DecayMeters(float decay)
        {
            SmoothedPeakLeft *= decay;
            SmoothedPeakRight *= decay;
            if (SmoothedPeakLeft < 0.0005f) SmoothedPeakLeft = 0f;
            if (SmoothedPeakRight < 0.0005f) SmoothedPeakRight = 0f;
        }
    }
}

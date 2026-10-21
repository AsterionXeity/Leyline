using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoidPatch.Models;

namespace VoidPatch.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = ChannelStripProcessor.EngineSampleRate;
    public static readonly WaveFormat Format = ChannelStripProcessor.EngineWaveFormat;

    private readonly object _engineLock = new();
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
    private System.Threading.Timer? _soundboardPumpTimer;
    private string _currentMonitorDeviceId = string.Empty;
    private string _currentStreamDeviceId = string.Empty;
    private int _listeningMixIndex; // 0: Personal Mix, 1: Stream Mix, 2: Discord Record Mix, 3: Mic Rec Mix

    private WaveFileWriter? _streamRecorder;
    private readonly object _recordLock = new();

    public SoundboardPlayer Soundboard { get; }
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
        Soundboard = new SoundboardPlayer(SampleRate);
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

        // Pump Soundboard samples directly into the Soundboard Channel Strip (20ms cadence = 960 stereo frames @ 48kHz)
        _soundboardPumpTimer = new System.Threading.Timer(PumpSoundboardToChannel, null, 20, 20);
    }

    private void PumpSoundboardToChannel(object? state)
    {
        const int frames = 960;
        float[] monBuf = new float[frames * 2];
        float[] strBuf = new float[frames * 2];
        Soundboard.MixNextBlock(monBuf, strBuf, frames);

        // Check if soundboard produced non-zero audio
        bool hasSignal = false;
        for (int i = 0; i < monBuf.Length; i++)
        {
            if (MathF.Abs(monBuf[i]) > 0.00001f)
            {
                hasSignal = true;
                break;
            }
        }
        if (!hasSignal) return;

        lock (_engineLock)
        {
            var sbChannel = _channels.FirstOrDefault(c =>
                c.Model.SourceType == ChannelSourceType.SoundboardChannel ||
                c.Model.Name.Equals("Soundboard", StringComparison.OrdinalIgnoreCase));

            sbChannel?.InjectExternalStereo48k(monBuf, frames);
        }
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
        return friendlyName.Contains("VoidPatch", StringComparison.OrdinalIgnoreCase) ||
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
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _monitorOut = null;
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

                ISampleProvider activeMixProvider = _listeningMixIndex switch
                {
                    1 => _streamMasterProvider,
                    2 => _discordMasterProvider,
                    3 => _micRecMasterProvider,
                    _ => _personalMasterProvider
                };

                _monitorOut = new WasapiOut(target, AudioClientShareMode.Shared, true, 25);
                _monitorOut.Init(activeMixProvider);
                _monitorOut.Play();
            }
            catch
            {
                _currentMonitorDeviceId = string.Empty;
            }
        }
    }

    public void SetStreamOutputDevice(string? deviceId)
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
                    _streamOut = new WasapiOut(target, AudioClientShareMode.Shared, true, 35);
                    _streamOut.Init(_streamMasterProvider);
                    _streamOut.Play();
                }
            }
            catch
            {
            }
        }
    }

    public ChannelStripProcessor AddOrRestartChannel(ChannelStripModel model)
    {
        lock (_engineLock)
        {
            RemoveChannel(model.Id);
            var proc = new ChannelStripProcessor(model, () => _currentMonitorDeviceId);
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

    public void UpdateAllMeters()
    {
        lock (_engineLock)
        {
            foreach (var ch in _channels)
            {
                ch.DecayAndPublishMeters();
            }
            _personalMasterProvider.DecayMeters();
            _streamMasterProvider.DecayMeters();
            _discordMasterProvider.DecayMeters();
            _micRecMasterProvider.DecayMeters();
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
                "VoidPatch Recordings");
            Directory.CreateDirectory(recDir);

            string filePath = Path.Combine(recDir, $"VoidPatch_StreamMix_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
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
        _soundboardPumpTimer?.Dispose();
        _soundboardPumpTimer = null;

        lock (_engineLock)
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _streamOut?.Stop();
            _streamOut?.Dispose();

            foreach (var ch in _channels)
                ch.Dispose();
            _channels.Clear();

            Soundboard.Dispose();
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
            return read;
        }

        public void DecayMeters()
        {
            SmoothedPeakLeft *= 0.88f;
            SmoothedPeakRight *= 0.88f;
            if (SmoothedPeakLeft < 0.0005f) SmoothedPeakLeft = 0f;
            if (SmoothedPeakRight < 0.0005f) SmoothedPeakRight = 0f;
        }
    }
}

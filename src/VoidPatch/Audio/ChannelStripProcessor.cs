using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoidPatch.Models;

namespace VoidPatch.Audio;

public sealed class ChannelStripProcessor : IDisposable
{
    public const int EngineSampleRate = 48000;
    public static readonly WaveFormat EngineWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(EngineSampleRate, 2);

    private readonly BufferedWaveProvider _personalBuffer;
    private readonly BufferedWaveProvider _streamBuffer;
    private readonly BufferedWaveProvider _discordBuffer;
    private readonly BufferedWaveProvider _micRecBuffer;

    private IWaveIn? _captureDevice;
    private readonly List<ProcessLoopbackCapture> _appCaptures = new();
    private volatile bool _isRunning;
    private float _smoothedPeakL;
    private float _smoothedPeakR;
    private readonly Func<string> _activeMonitorOutputIdGetter;

    public ChannelStripModel Model { get; }
    public DspChainProcessor DspProcessor { get; }

    public ISampleProvider MonitorOutput { get; }
    public ISampleProvider StreamOutput { get; }
    public ISampleProvider DiscordOutput { get; }
    public ISampleProvider MicRecOutput { get; }

    public ChannelStripProcessor(ChannelStripModel model, Func<string> activeMonitorOutputIdGetter)
    {
        Model = model;
        _activeMonitorOutputIdGetter = activeMonitorOutputIdGetter;
        DspProcessor = new DspChainProcessor(EngineSampleRate);

        _personalBuffer = CreateBusBuffer();
        _streamBuffer = CreateBusBuffer();
        _discordBuffer = CreateBusBuffer();
        _micRecBuffer = CreateBusBuffer();

        MonitorOutput = new BusBranchSampleProvider(_personalBuffer, () =>
            (Model.IsInputMuted || !Model.PersonalMix.IsEnabled || Model.PersonalMix.IsMuted)
                ? 0f
                : Model.InputVolume * Model.PersonalMix.Volume);

        StreamOutput = new BusBranchSampleProvider(_streamBuffer, () =>
            (Model.IsInputMuted || !Model.StreamMix.IsEnabled || Model.StreamMix.IsMuted)
                ? 0f
                : Model.InputVolume * Model.StreamMix.Volume);

        DiscordOutput = new BusBranchSampleProvider(_discordBuffer, () =>
            (Model.IsInputMuted || !Model.DiscordMix.IsEnabled || Model.DiscordMix.IsMuted)
                ? 0f
                : Model.InputVolume * Model.DiscordMix.Volume);

        MicRecOutput = new BusBranchSampleProvider(_micRecBuffer, () =>
            (Model.IsInputMuted || !Model.MicRecMix.IsEnabled || Model.MicRecMix.IsMuted)
                ? 0f
                : Model.InputVolume * Model.MicRecMix.Volume);
    }

    private static BufferedWaveProvider CreateBusBuffer() => new(EngineWaveFormat)
    {
        BufferDuration = TimeSpan.FromMilliseconds(160),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    public void Start()
    {
        Stop();
        _isRunning = true;
        DspProcessor.SyncVstSlots(Model.VstSlots);

        try
        {
            if (Model.SourceType == ChannelSourceType.SoundboardChannel)
            {
                Model.StatusText = "Built-in Soundboard";
                return;
            }

            if (Model.SourceType == ChannelSourceType.MicrophoneInput)
            {
                // Strictly hardware/virtual eCapture ONLY; never fallback to default if unselected
                StartStrictMicrophoneCapture();
                return;
            }

            // Start per-app process captures for any running AssignedApps or TargetProcessId
            StartAssignedAppCaptures();

            // Also capture assigned VoidPatch Virtual Bus if selected (and not the same as Monitoring Output)
            if (!string.IsNullOrWhiteSpace(Model.DeviceId))
            {
                StartVirtualBusLoopbackCapture();
            }
            else if (_appCaptures.Count == 0)
            {
                Model.StatusText = "Idle (No Device / Active App)";
            }
        }
        catch (Exception ex)
        {
            Model.StatusText = $"Error: {ex.Message}";
        }
    }

    private void StartStrictMicrophoneCapture()
    {
        if (string.IsNullOrWhiteSpace(Model.DeviceId))
        {
            Model.DeviceName = "None (Unselected)";
            Model.StatusText = "Unselected";
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try { device = enumerator.GetDevice(Model.DeviceId); } catch { }

        // STRICT FILTER: Must be an active Capture (eCapture) device, never a Render (output) device!
        if (device == null || device.State != DeviceState.Active || device.DataFlow != DataFlow.Capture)
        {
            Model.DeviceId = string.Empty;
            Model.DeviceName = "None (Unselected)";
            Model.StatusText = "No Input Selected";
            return;
        }

        Model.DeviceName = device.FriendlyName;
        var cap = new WasapiCapture(device, true, 20);
        cap.DataAvailable += OnCaptureDataAvailable;
        cap.RecordingStopped += OnCaptureStopped;
        _captureDevice = cap;
        cap.StartRecording();
        Model.StatusText = "Live Input";
    }

    private void StartVirtualBusLoopbackCapture()
    {
        if (string.IsNullOrWhiteSpace(Model.DeviceId))
            return;

        // Prevent loopback feedback: NEVER loopback-capture the hardware device currently used for Monitoring!
        string activeMonId = _activeMonitorOutputIdGetter();
        if (!string.IsNullOrWhiteSpace(activeMonId) &&
            Model.DeviceId.Equals(activeMonId, StringComparison.OrdinalIgnoreCase))
        {
            Model.StatusText = "Skipped (Monitoring Device)";
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try { device = enumerator.GetDevice(Model.DeviceId); } catch { }

        if (device == null || device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
        {
            Model.StatusText = _appCaptures.Count > 0 ? $"Apps ({_appCaptures.Count} active)" : "Unselected";
            return;
        }

        // Never use Elgato Virtual Audio endpoints
        if (device.FriendlyName.Contains("Elgato", StringComparison.OrdinalIgnoreCase))
        {
            Model.DeviceId = string.Empty;
            Model.DeviceName = "None (Unselected)";
            return;
        }

        Model.DeviceName = device.FriendlyName;
        var loop = new WasapiLoopbackCapture(device);
        loop.DataAvailable += OnCaptureDataAvailable;
        loop.RecordingStopped += OnCaptureStopped;
        _captureDevice = loop;
        loop.StartRecording();
        Model.StatusText = "Routing Active";
    }

    private void StartAssignedAppCaptures()
    {
        var pids = new HashSet<uint>();

        if (Model.TargetProcessId != 0)
            pids.Add(Model.TargetProcessId);

        foreach (var app in Model.AssignedApps)
        {
            try
            {
                string cleanName = app.ProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
                var procs = Process.GetProcessesByName(cleanName);
                var running = procs.FirstOrDefault(p => !p.HasExited);
                if (running != null)
                {
                    app.IsAvailable = true;
                    app.ActiveProcessId = (uint)running.Id;
                    pids.Add((uint)running.Id);
                }
                else
                {
                    app.IsAvailable = false;
                    app.ActiveProcessId = 0;
                }
            }
            catch
            {
                app.IsAvailable = false;
            }
        }

        foreach (uint pid in pids)
        {
            var procCap = new ProcessLoopbackCapture(pid, string.Empty);
            procCap.DataAvailable += OnCaptureDataAvailable;
            _appCaptures.Add(procCap);
            procCap.StartRecording();
        }

        if (_appCaptures.Count > 0)
        {
            Model.StatusText = $"Capturing {_appCaptures.Count} app(s)";
        }
    }

    public void InjectExternalStereo48k(ReadOnlySpan<float> stereoInterleaved, int sampleFrames)
    {
        if (!_isRunning || sampleFrames <= 0) return;
        float[] copy = stereoInterleaved[..(sampleFrames * 2)].ToArray();
        ProcessAndDispatchStereo(copy, sampleFrames);
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRunning || e.BytesRecorded <= 0)
            return;

        WaveFormat srcFmt = (sender as IWaveIn)?.WaveFormat ?? EngineWaveFormat;
        float[] stereo48k = ConvertToStereo48kFloat(e.Buffer, e.BytesRecorded, srcFmt, out int outFrames);
        if (outFrames > 0)
        {
            ProcessAndDispatchStereo(stereo48k, outFrames);
        }
    }

    private void ProcessAndDispatchStereo(float[] stereoBuffer, int sampleFrames)
    {
        Span<float> span = stereoBuffer.AsSpan(0, sampleFrames * 2);

        DspProcessor.ProcessStereoInterleaved(
            span,
            sampleFrames,
            Model.InputGainDb,
            Model.Dsp,
            Model.VstSlots,
            out float peakL,
            out float peakR);

        _smoothedPeakL = Math.Max(peakL, _smoothedPeakL * 0.85f);
        _smoothedPeakR = Math.Max(peakR, _smoothedPeakR * 0.85f);

        if (Model.IsInputMuted)
        {
            _personalBuffer.ClearBuffer();
            _streamBuffer.ClearBuffer();
            _discordBuffer.ClearBuffer();
            _micRecBuffer.ClearBuffer();
            return;
        }

        byte[] byteBytes = MemoryMarshal.AsBytes(span).ToArray();

        // Strictly gate each mix buffer so muted/disabled mixes receive ZERO samples (eliminates mix bleed completely)
        if (Model.PersonalMix.IsEnabled && !Model.PersonalMix.IsMuted)
            _personalBuffer.AddSamples(byteBytes, 0, byteBytes.Length);
        else
            _personalBuffer.ClearBuffer();

        if (Model.StreamMix.IsEnabled && !Model.StreamMix.IsMuted)
            _streamBuffer.AddSamples(byteBytes, 0, byteBytes.Length);
        else
            _streamBuffer.ClearBuffer();

        if (Model.DiscordMix.IsEnabled && !Model.DiscordMix.IsMuted)
            _discordBuffer.AddSamples(byteBytes, 0, byteBytes.Length);
        else
            _discordBuffer.ClearBuffer();

        if (Model.MicRecMix.IsEnabled && !Model.MicRecMix.IsMuted)
            _micRecBuffer.AddSamples(byteBytes, 0, byteBytes.Length);
        else
            _micRecBuffer.ClearBuffer();
    }

    public void DecayAndPublishMeters()
    {
        _smoothedPeakL *= 0.84f;
        _smoothedPeakR *= 0.84f;
        if (_smoothedPeakL < 0.0005f) _smoothedPeakL = 0f;
        if (_smoothedPeakR < 0.0005f) _smoothedPeakR = 0f;

        Model.PeakLeft = Model.IsInputMuted ? 0f : _smoothedPeakL * Model.InputVolume;
        Model.PeakRight = Model.IsInputMuted ? 0f : _smoothedPeakR * Model.InputVolume;
    }

    private static float[] ConvertToStereo48kFloat(byte[] rawBuffer, int bytesRecorded, WaveFormat fmt, out int outFrames)
    {
        int channels = Math.Max(1, fmt.Channels);
        int bits = fmt.BitsPerSample;
        int bytesPerSample = Math.Max(2, bits / 8);
        int frameSize = bytesPerSample * channels;
        int inFrames = bytesRecorded / frameSize;

        if (inFrames <= 0)
        {
            outFrames = 0;
            return [];
        }

        float[] inStereo = new float[inFrames * 2];
        Guid ieeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == ieeeFloatSubtype);

        for (int f = 0; f < inFrames; f++)
        {
            int frameOffset = f * frameSize;
            float left = ReadSampleAsFloat(rawBuffer, frameOffset, bits, isFloat);
            float right = channels >= 2
                ? ReadSampleAsFloat(rawBuffer, frameOffset + bytesPerSample, bits, isFloat)
                : left;

            inStereo[f * 2] = left;
            inStereo[f * 2 + 1] = right;
        }

        if (fmt.SampleRate == EngineSampleRate)
        {
            outFrames = inFrames;
            return inStereo;
        }

        double ratio = (double)EngineSampleRate / fmt.SampleRate;
        outFrames = Math.Max(1, (int)Math.Round(inFrames * ratio));
        float[] resampled = new float[outFrames * 2];

        for (int o = 0; o < outFrames; o++)
        {
            double srcPos = o / ratio;
            int idx0 = Math.Clamp((int)srcPos, 0, inFrames - 1);
            int idx1 = Math.Min(idx0 + 1, inFrames - 1);
            float frac = (float)(srcPos - idx0);

            resampled[o * 2] = inStereo[idx0 * 2] * (1f - frac) + inStereo[idx1 * 2] * frac;
            resampled[o * 2 + 1] = inStereo[idx0 * 2 + 1] * (1f - frac) + inStereo[idx1 * 2 + 1] * frac;
        }

        return resampled;
    }

    private static float ReadSampleAsFloat(byte[] buffer, int offset, int bits, bool isFloat)
    {
        if (offset + (bits / 8) > buffer.Length) return 0f;
        if (isFloat && bits == 32) return BitConverter.ToSingle(buffer, offset);
        if (bits == 16) return BitConverter.ToInt16(buffer, offset) / 32768f;
        if (bits == 24)
        {
            int s24 = (buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16));
            return s24 / 8388608f;
        }
        if (bits == 32) return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        return 0f;
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (_isRunning && e.Exception != null)
        {
            Model.StatusText = "Device Disconnected";
        }
    }

    public void Stop()
    {
        _isRunning = false;

        if (_captureDevice != null)
        {
            try
            {
                _captureDevice.DataAvailable -= OnCaptureDataAvailable;
                _captureDevice.RecordingStopped -= OnCaptureStopped;
                _captureDevice.StopRecording();
                _captureDevice.Dispose();
            }
            catch { }
            _captureDevice = null;
        }

        foreach (var appCap in _appCaptures)
        {
            try
            {
                appCap.DataAvailable -= OnCaptureDataAvailable;
                appCap.StopRecording();
                appCap.Dispose();
            }
            catch { }
        }
        _appCaptures.Clear();

        _personalBuffer.ClearBuffer();
        _streamBuffer.ClearBuffer();
        _discordBuffer.ClearBuffer();
        _micRecBuffer.ClearBuffer();
    }

    public void Dispose()
    {
        Stop();
        DspProcessor.Dispose();
    }

    private sealed class BusBranchSampleProvider : ISampleProvider
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly ISampleProvider _source;
        private readonly Func<float> _gainProvider;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public BusBranchSampleProvider(BufferedWaveProvider buffer, Func<float> gainProvider)
        {
            _buffer = buffer;
            _source = buffer.ToSampleProvider();
            _gainProvider = gainProvider;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            float gain = _gainProvider();
            if (gain <= 0.00001f)
            {
                // Drain buffer and output strict 0.0f silence so muted channels never bleed
                _buffer.ClearBuffer();
                Array.Clear(buffer, offset, count);
                return count;
            }

            int read = _source.Read(buffer, offset, count);
            if (MathF.Abs(gain - 1.0f) > 0.0001f)
            {
                for (int i = 0; i < read; i++)
                {
                    buffer[offset + i] *= gain;
                }
            }
            return read;
        }
    }
}

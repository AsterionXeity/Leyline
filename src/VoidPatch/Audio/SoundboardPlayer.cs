using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoidPatch.Models;

namespace VoidPatch.Audio;

public sealed class SoundboardPlayer : IDisposable
{
    private readonly int _sampleRate;
    private readonly object _lock = new();
    private readonly List<ActiveVoice> _voices = new();

    public float MasterVolume { get; set; } = 0.90f;

    public SoundboardPlayer(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
    }

    public void TriggerPad(SoundPadModel pad)
    {
        if (string.IsNullOrWhiteSpace(pad.FilePath) || !File.Exists(pad.FilePath))
            return;

        lock (_lock)
        {
            if (pad.PlaybackMode == PadPlaybackMode.StopToggle)
            {
                int removed = _voices.RemoveAll(v =>
                {
                    if (v.Pad.Id == pad.Id)
                    {
                        v.Dispose();
                        return true;
                    }
                    return false;
                });

                if (removed > 0)
                {
                    pad.IsPlaying = false;
                    return;
                }
            }
            else if (pad.PlaybackMode == PadPlaybackMode.OneShot || pad.PlaybackMode == PadPlaybackMode.Loop)
            {
                foreach (var v in _voices.Where(v => v.Pad.Id == pad.Id).ToList())
                {
                    v.Dispose();
                    _voices.Remove(v);
                }
            }

            try
            {
                var voice = new ActiveVoice(pad, _sampleRate);
                _voices.Add(voice);
                pad.IsPlaying = true;
            }
            catch
            {
                pad.IsPlaying = false;
            }
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var v in _voices)
            {
                v.Pad.IsPlaying = false;
                v.Dispose();
            }
            _voices.Clear();
        }
    }

    public void MixNextBlock(Span<float> monitorDest, Span<float> streamDest, int sampleFrames)
    {
        int floatCount = sampleFrames * 2;
        monitorDest[..floatCount].Clear();
        streamDest[..floatCount].Clear();

        lock (_lock)
        {
            if (_voices.Count == 0) return;

            float[] temp = new float[floatCount];
            float master = Math.Clamp(MasterVolume, 0f, 2f);

            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var voice = _voices[i];
                int read = voice.ReadStereoSamples(temp, floatCount);

                if (read <= 0)
                {
                    bool stillSamePadPlaying = _voices.Any(v => v != voice && v.Pad.Id == voice.Pad.Id);
                    if (!stillSamePadPlaying)
                        voice.Pad.IsPlaying = false;

                    voice.Dispose();
                    _voices.RemoveAt(i);
                    continue;
                }

                float gain = voice.Pad.Volume * master;
                bool toMon = voice.Pad.SendToMonitor;
                bool toStr = voice.Pad.SendToStream;

                for (int s = 0; s < read; s++)
                {
                    float sample = temp[s] * gain;
                    if (toMon) monitorDest[s] += sample;
                    if (toStr) streamDest[s] += sample;
                }
            }
        }
    }

    public void Dispose() => StopAll();

    private sealed class ActiveVoice : IDisposable
    {
        public SoundPadModel Pad { get; }
        private AudioFileReader? _reader;
        private ISampleProvider _provider;
        private readonly int _targetSampleRate;

        public ActiveVoice(SoundPadModel pad, int targetSampleRate)
        {
            Pad = pad;
            _targetSampleRate = targetSampleRate;
            _reader = new AudioFileReader(pad.FilePath);
            _provider = BuildPipeline(_reader, targetSampleRate);
        }

        private static ISampleProvider BuildPipeline(AudioFileReader reader, int targetSampleRate)
        {
            ISampleProvider current = reader;
            if (current.WaveFormat.Channels == 1)
                current = new MonoToStereoSampleProvider(current);
            if (current.WaveFormat.SampleRate != targetSampleRate)
                current = new WdlResamplingSampleProvider(current, targetSampleRate);
            return current;
        }

        public int ReadStereoSamples(float[] buffer, int count)
        {
            if (_reader == null) return 0;
            int read = _provider.Read(buffer, 0, count);

            if (read == 0 && Pad.PlaybackMode == PadPlaybackMode.Loop)
            {
                _reader.Position = 0;
                read = _provider.Read(buffer, 0, count);
            }

            return read;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}

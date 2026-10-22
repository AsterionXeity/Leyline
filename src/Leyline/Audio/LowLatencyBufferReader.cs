using NAudio.Wave;

namespace Leyline.Audio;

/// <summary>
/// Reads a <see cref="BufferedWaveProvider"/> that one thread fills (a capture) while an output drains it, and keeps
/// the audio waiting in it to a few milliseconds. The two sides start at different moments and can run on different
/// clocks (a USB headset against a Leyline cable), so without this the buffer settles at whatever it happened to
/// accumulate, up to its full size, and that stays as delay. Every half second this drops the part of the buffer no
/// read needed in that time: the smallest surplus left after a read, minus a small safety margin.
/// </summary>
public sealed class LowLatencyBufferReader : ISampleProvider
{
    private const double SafetyMs = 3.0;
    private const double WindowMs = 500.0;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _samples;
    private readonly int _blockAlign;
    private readonly int _bytesPerSample;
    private readonly int _safetyBytes;
    private readonly int _windowSamples;
    private int _minSurplus = int.MaxValue;
    private int _samplesSinceTrim;
    private byte[] _discard = [];

    public LowLatencyBufferReader(BufferedWaveProvider buffer)
    {
        _buffer = buffer;
        _samples = buffer.ToSampleProvider();
        var format = buffer.WaveFormat;
        _blockAlign = format.BlockAlign;
        _bytesPerSample = format.BitsPerSample / 8;
        _safetyBytes = (int)(format.AverageBytesPerSecond * SafetyMs / 1000.0) / _blockAlign * _blockAlign;
        _windowSamples = (int)(format.SampleRate * format.Channels * WindowMs / 1000.0);
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int surplus = _buffer.BufferedBytes - count * _bytesPerSample;
        if (surplus < _minSurplus)
            _minSurplus = surplus;

        _samplesSinceTrim += count;
        if (_samplesSinceTrim >= _windowSamples)
        {
            int excess = (_minSurplus - _safetyBytes) / _blockAlign * _blockAlign;
            if (excess > 0)
            {
                if (_discard.Length < excess)
                    _discard = new byte[excess];
                _buffer.Read(_discard, 0, excess);
            }
            _minSurplus = int.MaxValue;
            _samplesSinceTrim = 0;
        }

        return _samples.Read(buffer, offset, count);
    }
}

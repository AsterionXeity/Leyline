using System.IO;
using NAudio.Wave;
using Leyline.Models;

namespace Leyline.Audio;

public static class SampleGenerator
{
    public static string GetSamplesDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Leyline",
            "Soundboard",
            "Samples");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<SoundPadModel> EnsureDefaultPads()
    {
        string dir = GetSamplesDirectory();

        string airhornPath = Path.Combine(dir, "Airhorn.wav");
        string censorPath = Path.Combine(dir, "CensorBeep.wav");
        string subDropPath = Path.Combine(dir, "SubDropBoom.wav");
        string rimshotPath = Path.Combine(dir, "Rimshot.wav");
        string victoryPath = Path.Combine(dir, "VictoryChime.wav");
        string laserPath = Path.Combine(dir, "LaserZap.wav");
        string coinPath = Path.Combine(dir, "RetroCoin.wav");
        string suspensePath = Path.Combine(dir, "SuspenseHit.wav");

        GenerateIfMissing(airhornPath, 1.35f, GenerateAirhorn);
        GenerateIfMissing(censorPath, 0.75f, GenerateCensorBeep);
        GenerateIfMissing(subDropPath, 1.80f, GenerateSubDrop);
        GenerateIfMissing(rimshotPath, 1.10f, GenerateRimshot);
        GenerateIfMissing(victoryPath, 1.25f, GenerateVictoryChime);
        GenerateIfMissing(laserPath, 0.65f, GenerateLaserZap);
        GenerateIfMissing(coinPath, 0.55f, GenerateRetroCoin);
        GenerateIfMissing(suspensePath, 1.60f, GenerateSuspenseHit);

        // Win32 Modifiers: MOD_CONTROL (2) | MOD_SHIFT (4) = 6
        return
        [
            new SoundPadModel
            {
                Name = "MLG Airhorn",
                Emoji = "📢",
                ColorHex = "#FF4081",
                FilePath = airhornPath,
                Volume = 0.85f,
                HotkeyDisplay = "Ctrl+Shift+1",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x31
            },
            new SoundPadModel
            {
                Name = "Censor Beep",
                Emoji = "🤬",
                ColorHex = "#FF5252",
                FilePath = censorPath,
                Volume = 0.85f,
                HotkeyDisplay = "Ctrl+Shift+2",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x32
            },
            new SoundPadModel
            {
                Name = "Sub Bass Drop",
                Emoji = "💣",
                ColorHex = "#7C4DFF",
                FilePath = subDropPath,
                Volume = 0.95f,
                HotkeyDisplay = "Ctrl+Shift+3",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x33
            },
            new SoundPadModel
            {
                Name = "Ba-Dum Tss",
                Emoji = "🥁",
                ColorHex = "#FFD740",
                FilePath = rimshotPath,
                Volume = 0.90f,
                HotkeyDisplay = "Ctrl+Shift+4",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x34
            },
            new SoundPadModel
            {
                Name = "Victory Fanfare",
                Emoji = "🏆",
                ColorHex = "#00E676",
                FilePath = victoryPath,
                Volume = 0.85f,
                HotkeyDisplay = "Ctrl+Shift+5",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x35
            },
            new SoundPadModel
            {
                Name = "Sci-Fi Laser",
                Emoji = "⚡",
                ColorHex = "#00E5FF",
                FilePath = laserPath,
                Volume = 0.85f,
                HotkeyDisplay = "Ctrl+Shift+6",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x36
            },
            new SoundPadModel
            {
                Name = "8-Bit Coin",
                Emoji = "🪙",
                ColorHex = "#FFAB40",
                FilePath = coinPath,
                Volume = 0.85f,
                HotkeyDisplay = "Ctrl+Shift+7",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x37
            },
            new SoundPadModel
            {
                Name = "Dramatic Hit",
                Emoji = "🎬",
                ColorHex = "#E040FB",
                FilePath = suspensePath,
                Volume = 0.90f,
                HotkeyDisplay = "Ctrl+Shift+8",
                HotkeyModifiers = 6,
                HotkeyVirtualKey = 0x38
            }
        ];
    }

    private static void GenerateIfMissing(string path, float durationSeconds, Func<float, float, float> synth)
    {
        if (File.Exists(path)) return;
        try
        {
            const int sampleRate = 48000;
            int totalFrames = (int)(sampleRate * durationSeconds);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            using var writer = new WaveFileWriter(path, format);

            float[] buf = new float[totalFrames * 2];
            for (int i = 0; i < totalFrames; i++)
            {
                float t = (float)i / sampleRate;
                float s = Math.Clamp(synth(t, durationSeconds), -0.95f, 0.95f);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.WriteSamples(buf, 0, buf.Length);
        }
        catch { }
    }

    private static float GenerateAirhorn(float t, float dur)
    {
        // Classic multi-detuned saw/brass chord horn with double tap
        float env = t < 0.14f ? 1f : (t > 0.18f && t < 0.32f ? 1f : (t > 0.36f ? MathF.Exp(-(t - 0.36f) * 2.5f) : 0.1f));
        float f1 = 466.16f; // Bb4
        float f2 = 587.33f; // D5
        float f3 = 698.46f; // F5
        float wave =
            Saw(t * f1) * 0.35f +
            Saw(t * (f1 * 1.006f)) * 0.25f +
            Saw(t * f2) * 0.25f +
            Saw(t * f3) * 0.25f;
        return MathF.Tanh(wave * 1.8f) * env * 0.7f;
    }

    private static float GenerateCensorBeep(float t, float dur)
    {
        float env = t < 0.01f ? t / 0.01f : (t > dur - 0.02f ? (dur - t) / 0.02f : 1f);
        return MathF.Sin(2f * MathF.PI * 1000f * t) * env * 0.65f;
    }

    private static float GenerateSubDrop(float t, float dur)
    {
        float freq = 135f * MathF.Exp(-t * 1.35f) + 28f;
        float phase = 2f * MathF.PI * freq * t;
        float env = MathF.Exp(-t * 1.4f);
        return MathF.Tanh(MathF.Sin(phase) * 1.9f) * env * 0.85f;
    }

    private static float GenerateRimshot(float t, float dur)
    {
        // Kick (0.0s), Snare (0.18s), Hi-Hat/Cymbal splash (0.38s)
        float s = 0f;
        if (t < 0.18f)
        {
            float kt = t;
            float kFreq = 140f * MathF.Exp(-kt * 25f) + 45f;
            s += MathF.Sin(2f * MathF.PI * kFreq * kt) * MathF.Exp(-kt * 18f) * 0.9f;
        }
        if (t >= 0.18f && t < 0.38f)
        {
            float st = t - 0.18f;
            float body = MathF.Sin(2f * MathF.PI * 195f * st) * MathF.Exp(-st * 24f);
            float noise = (HashNoise(st) * 2f - 1f) * MathF.Exp(-st * 19f);
            s += (body * 0.5f + noise * 0.6f);
        }
        if (t >= 0.38f)
        {
            float ct = t - 0.38f;
            float shimmer = (HashNoise(ct * 1.7f) * 2f - 1f) * MathF.Sin(2f * MathF.PI * 5200f * ct);
            s += shimmer * MathF.Exp(-ct * 4.5f) * 0.65f;
        }
        return s;
    }

    private static float GenerateVictoryChime(float t, float dur)
    {
        // Arpeggio C5 -> E5 -> G5 -> C6
        float freq = t < 0.12f ? 523.25f : (t < 0.24f ? 659.25f : (t < 0.36f ? 783.99f : 1046.50f));
        float localT = t < 0.36f ? (t % 0.12f) : (t - 0.36f);
        float env = t < 0.36f ? MathF.Exp(-localT * 6f) : MathF.Exp(-localT * 3.2f);
        float tone = MathF.Sin(2f * MathF.PI * freq * t) + 0.35f * MathF.Sin(4f * MathF.PI * freq * t);
        return tone * env * 0.55f;
    }

    private static float GenerateLaserZap(float t, float dur)
    {
        float freq = 1800f * MathF.Exp(-t * 8.5f) + 90f;
        float env = MathF.Exp(-t * 4.2f);
        return Saw(t * freq) * env * 0.65f;
    }

    private static float GenerateRetroCoin(float t, float dur)
    {
        float freq = t < 0.09f ? 987.77f : 1318.51f; // B5 -> E6
        float env = MathF.Exp(-t * 5.0f);
        float sq = MathF.Sin(2f * MathF.PI * freq * t) >= 0f ? 0.6f : -0.6f;
        return sq * env;
    }

    private static float GenerateSuspenseHit(float t, float dur)
    {
        float env = MathF.Exp(-t * 2.2f);
        // Minor diminished cluster + sub impact
        float w =
            MathF.Sin(2f * MathF.PI * 65.41f * t) * 0.5f +
            Saw(t * 130.81f) * 0.25f +
            Saw(t * 155.56f) * 0.25f +
            Saw(t * 185.00f) * 0.25f;
        return MathF.Tanh(w * 2.0f) * env * 0.75f;
    }

    private static float Saw(float phaseCycles)
    {
        float frac = phaseCycles - MathF.Floor(phaseCycles);
        return 2f * frac - 1f;
    }

    private static float HashNoise(float t)
    {
        uint n = (uint)(t * 48000f) * 747796405u + 2891336453u;
        n = ((n >> ((int)((n >> 28) + 4))) ^ n) * 277803737u;
        n = (n >> 22) ^ n;
        return (n & 0xFFFFFF) / (float)0xFFFFFF;
    }
}

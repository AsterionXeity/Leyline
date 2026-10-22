using Leyline.Models;
using Leyline.Vst;

namespace Leyline.Audio;

public sealed class DspChainProcessor : IDisposable
{
    private readonly int _sampleRate;
    private readonly object _vstLock = new();
    private readonly Dictionary<string, VstHostInstance> _vstInstances = new();

    // Gate & Compressor envelope state
    private float _gateEnv;
    private float _compEnvDb = -96f;
    private float _limiterGain = 1.0f;

    // 3-Band Stereo Biquads (Low Shelf, Mid Peak, High Shelf)
    private BiquadFilter _lowL, _lowR;
    private BiquadFilter _midL, _midR;
    private BiquadFilter _highL, _highR;
    private float _lastLowDb = float.NaN;
    private float _lastMidDb = float.NaN;
    private float _lastHighDb = float.NaN;

    public DspChainProcessor(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        UpdateEqCoefficients(0f, 0f, 0f);
    }

    public VstHostInstance? GetOrLoadVst(VstSlotModel slot)
    {
        lock (_vstLock)
        {
            if (_vstInstances.TryGetValue(slot.Id, out var existing))
            {
                existing.IsBypassed = !slot.IsEnabled;
                existing.WetMix = slot.WetMix;
                return existing;
            }

            var instance = new VstHostInstance(slot.PluginPath, _sampleRate)
            {
                IsBypassed = !slot.IsEnabled,
                WetMix = slot.WetMix
            };

            // Restore saved normalized parameters if any
            if (instance.IsLoaded && slot.Parameters.Count > 0)
            {
                var paramList = instance.GetParameters();
                foreach (var kvp in slot.Parameters)
                {
                    var match = paramList.FirstOrDefault(p => p.Index == kvp.Key);
                    if (match != null)
                        instance.SetParameter(match.Id, match.Index, kvp.Value);
                }
            }

            _vstInstances[slot.Id] = instance;
            return instance;
        }
    }

    public void RemoveVst(string slotId)
    {
        lock (_vstLock)
        {
            if (_vstInstances.Remove(slotId, out var inst))
            {
                inst.Dispose();
            }
        }
    }

    public void SyncVstSlots(IEnumerable<VstSlotModel> slots)
    {
        lock (_vstLock)
        {
            var validIds = new HashSet<string>();
            foreach (var slot in slots)
            {
                validIds.Add(slot.Id);
                GetOrLoadVst(slot);
            }

            var toRemove = _vstInstances.Keys.Where(id => !validIds.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                if (_vstInstances.Remove(id, out var inst))
                    inst.Dispose();
            }
        }
    }

    public void ProcessStereoInterleaved(
        Span<float> buffer,
        int sampleFrames,
        float inputGainDb,
        DspChainModel dsp,
        IReadOnlyList<VstSlotModel>? vstSlots,
        out float peakLeft,
        out float peakRight)
    {
        peakLeft = 0f;
        peakRight = 0f;
        if (sampleFrames <= 0) return;

        float inputLinearGain = MathF.Abs(inputGainDb) < 0.01f ? 1.0f : DbToLinear(inputGainDb);

        // Precompute Gate coefficients
        bool gateOn = dsp.GateEnabled;
        float gateThreshLin = gateOn ? DbToLinear(dsp.GateThresholdDb) : 0f;
        float gateAttackCoeff = gateOn ? MathF.Exp(-1.0f / (Math.Max(1f, dsp.GateAttackMs) * 0.001f * _sampleRate)) : 0f;
        float gateReleaseCoeff = gateOn ? MathF.Exp(-1.0f / (Math.Max(5f, dsp.GateReleaseMs) * 0.001f * _sampleRate)) : 0f;

        // Precompute EQ coefficients if changed
        bool eqOn = dsp.EqEnabled;
        if (eqOn && (dsp.EqLowGainDb != _lastLowDb || dsp.EqMidGainDb != _lastMidDb || dsp.EqHighGainDb != _lastHighDb))
        {
            UpdateEqCoefficients(dsp.EqLowGainDb, dsp.EqMidGainDb, dsp.EqHighGainDb);
        }

        // Precompute Compressor coefficients
        bool compOn = dsp.CompEnabled;
        float compThreshDb = dsp.CompThresholdDb;
        float compRatio = Math.Max(1.0f, dsp.CompRatio);
        float compAttackCoeff = compOn ? MathF.Exp(-1.0f / (Math.Max(1f, dsp.CompAttackMs) * 0.001f * _sampleRate)) : 0f;
        float compReleaseCoeff = compOn ? MathF.Exp(-1.0f / (Math.Max(5f, dsp.CompReleaseMs) * 0.001f * _sampleRate)) : 0f;
        float compMakeupLin = compOn ? DbToLinear(dsp.CompMakeupGainDb) : 1.0f;

        for (int i = 0; i < sampleFrames; i++)
        {
            int idx = i * 2;
            float l = buffer[idx] * inputLinearGain;
            float r = buffer[idx + 1] * inputLinearGain;

            // 1. Noise Gate
            if (gateOn)
            {
                float absMax = MathF.Max(MathF.Abs(l), MathF.Abs(r));
                float targetGain = absMax >= gateThreshLin ? 1.0f : 0.0f;
                if (targetGain > _gateEnv)
                    _gateEnv = gateAttackCoeff * _gateEnv + (1.0f - gateAttackCoeff) * targetGain;
                else
                    _gateEnv = gateReleaseCoeff * _gateEnv + (1.0f - gateReleaseCoeff) * targetGain;

                l *= _gateEnv;
                r *= _gateEnv;
            }

            // 2. 3-Band Studio EQ
            if (eqOn)
            {
                l = _lowL.Process(l);
                r = _lowR.Process(r);
                l = _midL.Process(l);
                r = _midR.Process(r);
                l = _highL.Process(l);
                r = _highR.Process(r);
            }

            // 3. Studio Compressor
            if (compOn)
            {
                float absMax = MathF.Max(MathF.Abs(l), MathF.Abs(r));
                float inDb = absMax > 0.000001f ? 20f * MathF.Log10(absMax) : -96f;

                if (inDb > _compEnvDb)
                    _compEnvDb = compAttackCoeff * _compEnvDb + (1.0f - compAttackCoeff) * inDb;
                else
                    _compEnvDb = compReleaseCoeff * _compEnvDb + (1.0f - compReleaseCoeff) * inDb;

                float gainReductionDb = 0f;
                if (_compEnvDb > compThreshDb)
                {
                    float overDb = _compEnvDb - compThreshDb;
                    gainReductionDb = overDb * (1.0f - (1.0f / compRatio));
                }

                float totalGain = DbToLinear(-gainReductionDb) * compMakeupLin;
                l *= totalGain;
                r *= totalGain;
            }

            buffer[idx] = l;
            buffer[idx + 1] = r;
        }

        // 4. External VST2 / VST3 Plugin Chain
        if (vstSlots != null && vstSlots.Count > 0)
        {
            for (int s = 0; s < vstSlots.Count; s++)
            {
                var slot = vstSlots[s];
                if (!slot.IsEnabled) continue;

                VstHostInstance? inst;
                lock (_vstLock)
                {
                    _vstInstances.TryGetValue(slot.Id, out inst);
                }

                if (inst == null)
                    inst = GetOrLoadVst(slot);

                if (inst != null && inst.IsLoaded)
                {
                    inst.IsBypassed = !slot.IsEnabled;
                    inst.WetMix = slot.WetMix;
                    inst.ProcessStereo(buffer, sampleFrames);
                }
            }
        }

        // 5. Brickwall Limiter & Final Peak Metering
        bool limiterOn = dsp.LimiterEnabled;
        float ceilingLin = limiterOn ? DbToLinear(dsp.LimiterCeilingDb) : 1.0f;
        float limiterRelCoeff = MathF.Exp(-1.0f / (0.050f * _sampleRate));

        for (int i = 0; i < sampleFrames; i++)
        {
            int idx = i * 2;
            float l = buffer[idx];
            float r = buffer[idx + 1];

            if (!float.IsFinite(l)) l = 0f;
            if (!float.IsFinite(r)) r = 0f;

            if (limiterOn)
            {
                float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
                float desiredGain = peak > ceilingLin && peak > 0.0001f ? (ceilingLin / peak) : 1.0f;
                if (desiredGain < _limiterGain)
                    _limiterGain = desiredGain; // Instant brickwall attack
                else
                    _limiterGain = limiterRelCoeff * _limiterGain + (1.0f - limiterRelCoeff) * desiredGain;

                l = Math.Clamp(l * _limiterGain, -ceilingLin, ceilingLin);
                r = Math.Clamp(r * _limiterGain, -ceilingLin, ceilingLin);
            }

            buffer[idx] = l;
            buffer[idx + 1] = r;

            float absL = MathF.Abs(l);
            float absR = MathF.Abs(r);
            if (absL > peakLeft) peakLeft = absL;
            if (absR > peakRight) peakRight = absR;
        }
    }

    private void UpdateEqCoefficients(float lowDb, float midDb, float highDb)
    {
        _lastLowDb = lowDb;
        _lastMidDb = midDb;
        _lastHighDb = highDb;

        _lowL.SetLowShelf(_sampleRate, 100f, 0.707f, lowDb);
        _lowR.SetLowShelf(_sampleRate, 100f, 0.707f, lowDb);

        _midL.SetPeakingEq(_sampleRate, 1100f, 1.0f, midDb);
        _midR.SetPeakingEq(_sampleRate, 1100f, 1.0f, midDb);

        _highL.SetHighShelf(_sampleRate, 8000f, 0.707f, highDb);
        _highR.SetHighShelf(_sampleRate, 8000f, 0.707f, highDb);
    }

    private static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

    public void Dispose()
    {
        lock (_vstLock)
        {
            foreach (var inst in _vstInstances.Values)
            {
                inst.Dispose();
            }
            _vstInstances.Clear();
        }
    }

    private struct BiquadFilter
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public void SetPeakingEq(int sampleRate, float freq, float q, float gainDb)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freq / sampleRate;
            float cosw0 = MathF.Cos(w0);
            float sinw0 = MathF.Sin(w0);
            float alpha = sinw0 / (2f * q);

            float b0 = 1f + alpha * a;
            float b1 = -2f * cosw0;
            float b2 = 1f - alpha * a;
            float a0 = 1f + alpha / a;
            float a1 = -2f * cosw0;
            float a2 = 1f - alpha / a;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public void SetLowShelf(int sampleRate, float freq, float q, float gainDb)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freq / sampleRate;
            float cosw0 = MathF.Cos(w0);
            float sinw0 = MathF.Sin(w0);
            float alpha = sinw0 / (2f * q);
            float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

            float b0 = a * ((a + 1f) - (a - 1f) * cosw0 + twoSqrtAAlpha);
            float b1 = 2f * a * ((a - 1f) - (a + 1f) * cosw0);
            float b2 = a * ((a + 1f) - (a - 1f) * cosw0 - twoSqrtAAlpha);
            float a0 = (a + 1f) + (a - 1f) * cosw0 + twoSqrtAAlpha;
            float a1 = -2f * ((a - 1f) + (a + 1f) * cosw0);
            float a2 = (a + 1f) + (a - 1f) * cosw0 - twoSqrtAAlpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public void SetHighShelf(int sampleRate, float freq, float q, float gainDb)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * freq / sampleRate;
            float cosw0 = MathF.Cos(w0);
            float sinw0 = MathF.Sin(w0);
            float alpha = sinw0 / (2f * q);
            float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

            float b0 = a * ((a + 1f) + (a - 1f) * cosw0 + twoSqrtAAlpha);
            float b1 = -2f * a * ((a - 1f) + (a + 1f) * cosw0);
            float b2 = a * ((a + 1f) + (a - 1f) * cosw0 - twoSqrtAAlpha);
            float a0 = (a + 1f) - (a - 1f) * cosw0 + twoSqrtAAlpha;
            float a1 = 2f * ((a - 1f) - (a + 1f) * cosw0);
            float a2 = (a + 1f) - (a - 1f) * cosw0 - twoSqrtAAlpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }
    }
}

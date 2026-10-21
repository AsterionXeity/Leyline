using System.Text.Json.Serialization;
using VoidPatch.ViewModels;

namespace VoidPatch.Models;

public sealed class DspChainModel : ObservableObject
{
    // 1. Noise Gate
    private bool _gateEnabled;
    private float _gateThresholdDb = -42f;
    private float _gateAttackMs = 5f;
    private float _gateReleaseMs = 120f;

    // 2. 3-Band Studio EQ
    private bool _eqEnabled;
    private float _eqLowGainDb;   // 100 Hz Low Shelf (-15 to +15 dB)
    private float _eqMidGainDb;   // 1100 Hz Peaking (-15 to +15 dB)
    private float _eqHighGainDb;  // 8000 Hz High Shelf (-15 to +15 dB)

    // 3. Studio Compressor
    private bool _compEnabled;
    private float _compThresholdDb = -18f;
    private float _compRatio = 4.0f;
    private float _compAttackMs = 10f;
    private float _compReleaseMs = 100f;
    private float _compMakeupGainDb = 3.0f;

    // 4. Brickwall Limiter
    private bool _limiterEnabled = true;
    private float _limiterCeilingDb = -1.0f;

    public bool GateEnabled
    {
        get => _gateEnabled;
        set { if (SetProperty(ref _gateEnabled, value)) OnPropertyChanged(nameof(ActiveCount)); }
    }
    public float GateThresholdDb
    {
        get => _gateThresholdDb;
        set
        {
            if (SetProperty(ref _gateThresholdDb, Math.Clamp(value, -80f, 0f)))
            {
                OnPropertyChanged(nameof(GateThresholdDbDouble));
                OnPropertyChanged(nameof(GateThresholdDbText));
            }
        }
    }
    [JsonIgnore]
    public double GateThresholdDbDouble
    {
        get => _gateThresholdDb;
        set => GateThresholdDb = (float)value;
    }
    [JsonIgnore]
    public string GateThresholdDbText => $"{_gateThresholdDb:0} dB";

    public float GateAttackMs
    {
        get => _gateAttackMs;
        set
        {
            if (SetProperty(ref _gateAttackMs, Math.Clamp(value, 1f, 200f)))
            {
                OnPropertyChanged(nameof(GateAttackMsDouble));
                OnPropertyChanged(nameof(GateAttackMsText));
            }
        }
    }
    [JsonIgnore]
    public double GateAttackMsDouble
    {
        get => _gateAttackMs;
        set => GateAttackMs = (float)value;
    }
    [JsonIgnore]
    public string GateAttackMsText => $"{_gateAttackMs:0} ms";

    public float GateReleaseMs
    {
        get => _gateReleaseMs;
        set
        {
            if (SetProperty(ref _gateReleaseMs, Math.Clamp(value, 10f, 1000f)))
            {
                OnPropertyChanged(nameof(GateReleaseMsDouble));
                OnPropertyChanged(nameof(GateReleaseMsText));
            }
        }
    }
    [JsonIgnore]
    public double GateReleaseMsDouble
    {
        get => _gateReleaseMs;
        set => GateReleaseMs = (float)value;
    }
    [JsonIgnore]
    public string GateReleaseMsText => $"{_gateReleaseMs:0} ms";

    public bool EqEnabled
    {
        get => _eqEnabled;
        set { if (SetProperty(ref _eqEnabled, value)) OnPropertyChanged(nameof(ActiveCount)); }
    }
    public float EqLowGainDb
    {
        get => _eqLowGainDb;
        set
        {
            if (SetProperty(ref _eqLowGainDb, Math.Clamp(value, -15f, 15f)))
            {
                OnPropertyChanged(nameof(EqLowGainDbDouble));
                OnPropertyChanged(nameof(EqLowGainDbText));
            }
        }
    }
    [JsonIgnore]
    public double EqLowGainDbDouble
    {
        get => _eqLowGainDb;
        set => EqLowGainDb = (float)value;
    }
    [JsonIgnore]
    public string EqLowGainDbText => $"{_eqLowGainDb:+0.0;-0.0;0.0} dB";

    public float EqMidGainDb
    {
        get => _eqMidGainDb;
        set
        {
            if (SetProperty(ref _eqMidGainDb, Math.Clamp(value, -15f, 15f)))
            {
                OnPropertyChanged(nameof(EqMidGainDbDouble));
                OnPropertyChanged(nameof(EqMidGainDbText));
            }
        }
    }
    [JsonIgnore]
    public double EqMidGainDbDouble
    {
        get => _eqMidGainDb;
        set => EqMidGainDb = (float)value;
    }
    [JsonIgnore]
    public string EqMidGainDbText => $"{_eqMidGainDb:+0.0;-0.0;0.0} dB";

    public float EqHighGainDb
    {
        get => _eqHighGainDb;
        set
        {
            if (SetProperty(ref _eqHighGainDb, Math.Clamp(value, -15f, 15f)))
            {
                OnPropertyChanged(nameof(EqHighGainDbDouble));
                OnPropertyChanged(nameof(EqHighGainDbText));
            }
        }
    }
    [JsonIgnore]
    public double EqHighGainDbDouble
    {
        get => _eqHighGainDb;
        set => EqHighGainDb = (float)value;
    }
    [JsonIgnore]
    public string EqHighGainDbText => $"{_eqHighGainDb:+0.0;-0.0;0.0} dB";

    public bool CompEnabled
    {
        get => _compEnabled;
        set { if (SetProperty(ref _compEnabled, value)) OnPropertyChanged(nameof(ActiveCount)); }
    }
    public float CompThresholdDb
    {
        get => _compThresholdDb;
        set
        {
            if (SetProperty(ref _compThresholdDb, Math.Clamp(value, -60f, 0f)))
            {
                OnPropertyChanged(nameof(CompThresholdDbDouble));
                OnPropertyChanged(nameof(CompThresholdDbText));
            }
        }
    }
    [JsonIgnore]
    public double CompThresholdDbDouble
    {
        get => _compThresholdDb;
        set => CompThresholdDb = (float)value;
    }
    [JsonIgnore]
    public string CompThresholdDbText => $"{_compThresholdDb:0} dB";

    public float CompRatio
    {
        get => _compRatio;
        set
        {
            if (SetProperty(ref _compRatio, Math.Clamp(value, 1.0f, 20.0f)))
            {
                OnPropertyChanged(nameof(CompRatioDouble));
                OnPropertyChanged(nameof(CompRatioText));
            }
        }
    }
    [JsonIgnore]
    public double CompRatioDouble
    {
        get => _compRatio;
        set => CompRatio = (float)value;
    }
    [JsonIgnore]
    public string CompRatioText => $"{_compRatio:0.0}:1";

    public float CompAttackMs
    {
        get => _compAttackMs;
        set
        {
            if (SetProperty(ref _compAttackMs, Math.Clamp(value, 1f, 200f)))
            {
                OnPropertyChanged(nameof(CompAttackMsDouble));
                OnPropertyChanged(nameof(CompAttackMsText));
            }
        }
    }
    [JsonIgnore]
    public double CompAttackMsDouble
    {
        get => _compAttackMs;
        set => CompAttackMs = (float)value;
    }
    [JsonIgnore]
    public string CompAttackMsText => $"{_compAttackMs:0} ms";

    public float CompReleaseMs
    {
        get => _compReleaseMs;
        set
        {
            if (SetProperty(ref _compReleaseMs, Math.Clamp(value, 10f, 1000f)))
            {
                OnPropertyChanged(nameof(CompReleaseMsDouble));
                OnPropertyChanged(nameof(CompReleaseMsText));
            }
        }
    }
    [JsonIgnore]
    public double CompReleaseMsDouble
    {
        get => _compReleaseMs;
        set => CompReleaseMs = (float)value;
    }
    [JsonIgnore]
    public string CompReleaseMsText => $"{_compReleaseMs:0} ms";

    public float CompMakeupGainDb
    {
        get => _compMakeupGainDb;
        set
        {
            if (SetProperty(ref _compMakeupGainDb, Math.Clamp(value, -12f, 24f)))
            {
                OnPropertyChanged(nameof(CompMakeupGainDbDouble));
                OnPropertyChanged(nameof(CompMakeupGainDbText));
            }
        }
    }
    [JsonIgnore]
    public double CompMakeupGainDbDouble
    {
        get => _compMakeupGainDb;
        set => CompMakeupGainDb = (float)value;
    }
    [JsonIgnore]
    public string CompMakeupGainDbText => $"{_compMakeupGainDb:+0.0;-0.0;0.0} dB";

    public bool LimiterEnabled
    {
        get => _limiterEnabled;
        set { if (SetProperty(ref _limiterEnabled, value)) OnPropertyChanged(nameof(ActiveCount)); }
    }
    public float LimiterCeilingDb
    {
        get => _limiterCeilingDb;
        set
        {
            if (SetProperty(ref _limiterCeilingDb, Math.Clamp(value, -18f, 0f)))
            {
                OnPropertyChanged(nameof(LimiterCeilingDbDouble));
                OnPropertyChanged(nameof(LimiterCeilingDbText));
            }
        }
    }
    [JsonIgnore]
    public double LimiterCeilingDbDouble
    {
        get => _limiterCeilingDb;
        set => LimiterCeilingDb = (float)value;
    }
    [JsonIgnore]
    public string LimiterCeilingDbText => $"{_limiterCeilingDb:0.0} dB";

    [JsonIgnore]
    public int ActiveCount =>
        (GateEnabled ? 1 : 0) +
        (EqEnabled ? 1 : 0) +
        (CompEnabled ? 1 : 0) +
        (LimiterEnabled ? 1 : 0);

    public void ResetGate()
    {
        GateThresholdDb = -42f;
        GateAttackMs = 5f;
        GateReleaseMs = 120f;
    }

    public void ResetEq()
    {
        EqLowGainDb = 0f;
        EqMidGainDb = 0f;
        EqHighGainDb = 0f;
    }

    public void ResetComp()
    {
        CompThresholdDb = -18f;
        CompRatio = 4.0f;
        CompAttackMs = 10f;
        CompReleaseMs = 100f;
        CompMakeupGainDb = 3.0f;
    }

    public void ResetLimiter()
    {
        LimiterCeilingDb = -1.0f;
    }

    public void ResetAll()
    {
        GateEnabled = false;
        EqEnabled = false;
        CompEnabled = false;
        LimiterEnabled = true;
        ResetGate();
        ResetEq();
        ResetComp();
        ResetLimiter();
    }

    public void ApplyPreset(string presetKey)
    {
        switch (presetKey.ToLowerInvariant())
        {
            case "broadcast":
                GateEnabled = true;
                GateThresholdDb = -40f;
                GateAttackMs = 4f;
                GateReleaseMs = 130f;
                EqEnabled = true;
                EqLowGainDb = 2.5f;
                EqMidGainDb = 1.5f;
                EqHighGainDb = 3.0f;
                CompEnabled = true;
                CompThresholdDb = -20f;
                CompRatio = 4.5f;
                CompAttackMs = 8f;
                CompReleaseMs = 110f;
                CompMakeupGainDb = 3.5f;
                LimiterEnabled = true;
                LimiterCeilingDb = -1.0f;
                break;

            case "warmmic":
                GateEnabled = true;
                GateThresholdDb = -44f;
                GateAttackMs = 5f;
                GateReleaseMs = 150f;
                EqEnabled = true;
                EqLowGainDb = 3.5f;
                EqMidGainDb = -1.0f;
                EqHighGainDb = 1.5f;
                CompEnabled = true;
                CompThresholdDb = -18f;
                CompRatio = 3.0f;
                CompAttackMs = 12f;
                CompReleaseMs = 120f;
                CompMakeupGainDb = 2.5f;
                LimiterEnabled = true;
                LimiterCeilingDb = -1.0f;
                break;

            case "gaming":
                GateEnabled = true;
                GateThresholdDb = -38f;
                GateAttackMs = 3f;
                GateReleaseMs = 95f;
                EqEnabled = true;
                EqLowGainDb = -2.0f;
                EqMidGainDb = 3.0f;
                EqHighGainDb = 2.5f;
                CompEnabled = true;
                CompThresholdDb = -16f;
                CompRatio = 3.5f;
                CompAttackMs = 6f;
                CompReleaseMs = 90f;
                CompMakeupGainDb = 2.0f;
                LimiterEnabled = true;
                LimiterCeilingDb = -0.5f;
                break;

            case "music":
                GateEnabled = false;
                EqEnabled = true;
                EqLowGainDb = 4.5f;
                EqMidGainDb = 0.5f;
                EqHighGainDb = 2.5f;
                CompEnabled = false;
                LimiterEnabled = true;
                LimiterCeilingDb = -0.5f;
                break;

            case "podcast":
                GateEnabled = true;
                GateThresholdDb = -42f;
                GateAttackMs = 5f;
                GateReleaseMs = 140f;
                EqEnabled = true;
                EqLowGainDb = 1.5f;
                EqMidGainDb = 2.0f;
                EqHighGainDb = 2.0f;
                CompEnabled = true;
                CompThresholdDb = -24f;
                CompRatio = 6.0f;
                CompAttackMs = 8f;
                CompReleaseMs = 120f;
                CompMakeupGainDb = 5.0f;
                LimiterEnabled = true;
                LimiterCeilingDb = -1.0f;
                break;

            default:
                ResetAll();
                break;
        }
    }
}

public sealed class VstSlotModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _pluginName = string.Empty;
    private string _pluginPath = string.Empty;
    private string _format = "VST2"; // VST2 or VST3
    private bool _isEnabled = true;
    private float _wetMix = 1.0f;
    private Dictionary<int, float> _parameters = new();

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string PluginName
    {
        get => _pluginName;
        set => SetProperty(ref _pluginName, value);
    }

    public string PluginPath
    {
        get => _pluginPath;
        set => SetProperty(ref _pluginPath, value);
    }

    public string Format
    {
        get => _format;
        set => SetProperty(ref _format, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public float WetMix
    {
        get => _wetMix;
        set
        {
            if (SetProperty(ref _wetMix, Math.Clamp(value, 0f, 1f)))
                OnPropertyChanged(nameof(WetMixDouble));
        }
    }

    [JsonIgnore]
    public double WetMixDouble
    {
        get => _wetMix;
        set => WetMix = (float)value;
    }

    public Dictionary<int, float> Parameters
    {
        get => _parameters;
        set => SetProperty(ref _parameters, value);
    }
}

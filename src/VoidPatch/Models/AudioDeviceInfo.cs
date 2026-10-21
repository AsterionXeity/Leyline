namespace VoidPatch.Models;

public enum AudioEndpointKind
{
    None,
    CaptureInput,
    RenderOutput,
    VirtualEndpoint
}

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AudioEndpointKind Kind { get; set; }
    public bool IsDefault { get; set; }
    public bool IsVirtual { get; set; }

    public bool IsNone => string.IsNullOrEmpty(Id);

    public string DisplayBadge => IsNone
        ? "OFF"
        : (IsVirtual ? "VOIDPATCH" : (Kind == AudioEndpointKind.CaptureInput ? "INPUT" : "OUTPUT"));

    public static AudioDeviceInfo CreateNone(string label = "None (Unselected)") => new()
    {
        Id = string.Empty,
        Name = label,
        Kind = AudioEndpointKind.None,
        IsDefault = false,
        IsVirtual = false
    };

    public override string ToString() => Name;
}

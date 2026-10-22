namespace Leyline.Models;

/// <summary>
/// Icons the channel and mix icon pickers offer: Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10)
/// glyphs, stored as the glyph character itself in a channel's or mix's Icon.
/// </summary>
public static class IconGlyphs
{
    public static IReadOnlyList<string> Choices { get; } =
    [
        "\uE7F6", // headphones
        "\uE7F5", // speakers
        "\uE767", // volume
        "\uE720", // microphone
        "\uE7C8", // record
        "\uE701", // broadcast
        "\uE716", // people
        "\uE8BD", // chat
        "\uE7FC", // game
        "\uEC4F", // music
        "\uE8D6", // audio
        "\uE9E9", // equalizer
        "\uE774", // browser
        "\uE7F4", // monitor
        "\uE714", // video
        "\uE80F", // home
        "\uE734", // star
        "\uEB51"  // heart
    ];

    /// <summary>True for a glyph from the icon font (Private Use Area), as the pickers store.</summary>
    public static bool IsGlyph(string? icon) =>
        icon is { Length: 1 } && icon[0] >= '\uE000' && icon[0] <= '\uF8FF';
}

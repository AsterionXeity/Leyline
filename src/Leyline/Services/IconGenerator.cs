using System.Drawing;
using System.IO;

namespace Leyline.Services;

/// <summary>
/// The Leyline app icon (Assets\Leyline.ico / Leyline.png, copied next to Leyline.exe; also the exe's own icon).
/// </summary>
public static class IconGenerator
{
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Leyline.ico");
    public static string PngPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Leyline.png");

    /// <summary>The tray icon (32 px frame of the .ico; Windows scales it for the tray).</summary>
    public static Icon CreateAppIcon()
    {
        try
        {
            return new Icon(IconPath, 32, 32);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}

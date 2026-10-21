using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace VoidPatch.Services;

public static class ThemeService
{
    public static bool CurrentIsLightMode { get; private set; }

    public static event Action<bool>? ThemeChanged;

    public static void ApplyTheme(bool isLightMode)
    {
        CurrentIsLightMode = isLightMode;
        ThemeChanged?.Invoke(isLightMode);
    }

    public static Color ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Color.FromArgb(255, 68, 138, 255);

        string s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s[..2], 16);
                byte g = Convert.ToByte(s.Substring(2, 2), 16);
                byte b = Convert.ToByte(s.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            if (s.Length == 8)
            {
                byte a = Convert.ToByte(s[..2], 16);
                byte r = Convert.ToByte(s.Substring(2, 2), 16);
                byte g = Convert.ToByte(s.Substring(4, 2), 16);
                byte b = Convert.ToByte(s.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
        }
        return Color.FromArgb(255, 68, 138, 255);
    }

    public static SolidColorBrush CreateBrush(string hex) => new(ParseHexColor(hex));
}

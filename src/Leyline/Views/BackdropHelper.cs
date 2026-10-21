using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leyline.Views;

/// <summary>
/// Applies a subtle blurred system backdrop: Mica on Windows 11, Desktop Acrylic on Windows 10.
/// Window/sidebar brushes are translucent so the backdrop shows through.
/// </summary>
public static class BackdropHelper
{
    public static void Apply(Window window)
    {
        try
        {
            if (MicaController.IsSupported())
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            else if (DesktopAcrylicController.IsSupported())
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Backdrop is cosmetic; fall back to the solid theme brushes.
        }
    }
}

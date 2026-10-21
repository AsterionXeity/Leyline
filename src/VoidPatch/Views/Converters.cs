using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using VoidPatch.Services;

namespace VoidPatch.Views;

public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            return ThemeService.CreateBrush(hex);
        return ThemeService.CreateBrush("#448AFF");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => "#448AFF";
}

/// <summary>
/// Converts MixSendState.CellStateKey ("Disabled", "Muted", "Active") into the exact surface brush
/// from the reference screenshots (#1E1E21 disabled, #442222 muted maroon, #262629 active card).
/// </summary>
public sealed class MixCellBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string state = value as string ?? "Active";
        bool isLight = ThemeService.CurrentIsLightMode;

        return state switch
        {
            "Disabled" => ThemeService.CreateBrush(isLight ? "#EBEEF5" : "#1E1E21"),
            "Muted" => ThemeService.CreateBrush(isLight ? "#FCE8E6" : "#442222"),
            _ => ThemeService.CreateBrush(isLight ? "#FFFFFF" : "#262629")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => "Active";
}

public sealed class MixMutedCellBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isMuted = value is bool b && b;
        bool isLight = ThemeService.CurrentIsLightMode;
        if (isMuted)
            return ThemeService.CreateBrush(isLight ? "#FCE8E6" : "#442222");
        return ThemeService.CreateBrush(isLight ? "#FFFFFF" : "#262629");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => false;
}

public sealed class MixMutedBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isMuted = value is bool b && b;
        bool isLight = ThemeService.CurrentIsLightMode;
        if (isMuted)
            return ThemeService.CreateBrush(isLight ? "#D93025" : "#EF5350");
        return ThemeService.CreateBrush("#00000000");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => false;
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isEnabled = value is not bool b || b;
        return isEnabled ? 1.0 : 0.55;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => true;
}

public sealed class ActiveFxBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasFx = value is bool b && b;
        bool isLight = ThemeService.CurrentIsLightMode;
        if (hasFx)
            return ThemeService.CreateBrush(isLight ? "#1E8E3E" : "#2BD968");
        return ThemeService.CreateBrush(isLight ? "#44474F" : "#D1D1D6");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => false;
}

public sealed class MuteIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "\uE74F" : "\uE767";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => false;
}

public sealed class MuteToggleBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isMuted = value is bool b && b;
        bool isLight = ThemeService.CurrentIsLightMode;
        if (isMuted)
            return ThemeService.CreateBrush(isLight ? "#FAD2CF" : "#28EF5350");
        return ThemeService.CreateBrush("#00000000");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => false;
}

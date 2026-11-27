using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Alondra1.Core.Config;

namespace Alondra1.App;

public static class ThemeService
{
    /// <summary>Demo snapshots: apply the high-contrast mapping even when Windows is not in a contrast theme.</summary>
    public static bool ForceHighContrast { get; set; }

    public static bool IsHighContrast => ForceHighContrast || SystemParameters.HighContrast;

    /// <summary>Re-applies the theme when Windows turns a contrast theme on or off.</summary>
    public static void Watch(Func<AppearanceSettings> appearance) =>
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(appearance()));
        };

    public static void Apply(AppearanceSettings appearance)
    {
        var light = appearance.Theme switch
        {
            ThemePreference.Light => true,
            ThemePreference.Dark => false,
            _ => SystemIsLight(),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = Defaults(light);
        var colors = light ? appearance.LightColors : appearance.DarkColors;
        foreach (var (key, value) in colors ?? [])
            if (palette[key] is SolidColorBrush && IsHexColor(value))
                palette[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        // Over a background picture, main panels are 88% opaque: the picture shows in the gaps and faintly behind the
        // panels, while text (including muted dates and metadata) keeps at least 4.5:1 contrast over any picture, even a
        // pure white or black area (checked by tests). The header gets a light scrim of the base color behind its labels.
        // Popups, menus, and inputs keep the opaque SurfaceBrush.
        var active = Shell.BackgroundService.IsActive(appearance.Background);
        if (palette["SurfaceBrush"] is SolidColorBrush surface)
        {
            var color = surface.Color;
            if (active)
                color.A = PanelAlphaOverPicture;
            palette["PanelBrush"] = new SolidColorBrush(color);
        }

        if (palette["ChromeBrush"] is SolidColorBrush chrome)
        {
            var scrim = chrome.Color;
            scrim.A = active ? (byte)0x73 : (byte)0x00;
            palette["HeaderScrimBrush"] = new SolidColorBrush(scrim);
        }

        if (IsHighContrast)
            ApplyHighContrast(palette);
        dictionaries[0] = palette;
        // Native title bars of open dialogs follow the new palette too.
        Shell.ShellWindow.ApplyCaptionToAll();
        // The picture's brightness adapts to the theme, so it is re-processed when the theme changes.
        (Application.Current.MainWindow as MainWindow)?.ApplyBackground();
    }

    private static void ApplyHighContrast(ResourceDictionary palette)
    {
        foreach (var (key, role) in Alondra1.Core.Layout.HighContrast.Roles)
        {
            var color = role switch
            {
                Alondra1.Core.Layout.ContrastRole.Window => SystemColors.WindowColor,
                Alondra1.Core.Layout.ContrastRole.WindowText => SystemColors.WindowTextColor,
                Alondra1.Core.Layout.ContrastRole.Highlight => SystemColors.HighlightColor,
                Alondra1.Core.Layout.ContrastRole.HighlightText => SystemColors.HighlightTextColor,
                Alondra1.Core.Layout.ContrastRole.HotTrack => SystemColors.HotTrackColor,
                _ => Colors.Transparent,
            };
            palette[key] = new SolidColorBrush(color);
        }
    }

    /// <summary>Panel opacity over a background picture (0xE0 = 88 %).</summary>
    public const byte PanelAlphaOverPicture = 0xE0;

    public static bool IsDark => Application.Current.Resources.MergedDictionaries[0]["ChromeBrush"] is SolidColorBrush { Color: var c } && c.R + c.G + c.B < 200;

    public static ResourceDictionary Defaults(bool light) => new()
    {
        Source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative),
    };

    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    public static bool SystemIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}

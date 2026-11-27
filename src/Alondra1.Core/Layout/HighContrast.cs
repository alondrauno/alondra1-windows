namespace Alondra1.Core.Layout;

/// <summary>The Windows high-contrast system colors a theme brush is replaced with.</summary>
public enum ContrastRole { Window, WindowText, Highlight, HighlightText, HotTrack, Transparent }

/// <summary>
/// Windows high-contrast themes (Mail UI spec §9): every app brush maps to a system color, so the user's chosen
/// contrast theme applies to the whole window. Text, muted text, lines and status colors all become WindowText (state is
/// never told by color alone: unread has a dot and weight, selection an edge, failures text); surfaces become Window;
/// the accent (selection edge, unread dot, primary buttons) becomes Highlight; links HotTrack. Pictures are not shown.
/// </summary>
public static class HighContrast
{
    public static IReadOnlyDictionary<string, ContrastRole> Roles { get; } = new Dictionary<string, ContrastRole>(StringComparer.Ordinal)
    {
        ["ChromeBrush"] = ContrastRole.Window,
        ["SurfaceBrush"] = ContrastRole.Window,
        ["PanelBrush"] = ContrastRole.Window,
        ["HeaderScrimBrush"] = ContrastRole.Transparent,
        ["SubtleBrush"] = ContrastRole.Window,
        ["WorkspaceTabBrush"] = ContrastRole.Window,
        ["HoverBrush"] = ContrastRole.Window,
        ["SelectedBrush"] = ContrastRole.Window,
        ["DangerSoftBrush"] = ContrastRole.Window,
        ["TextBrush"] = ContrastRole.WindowText,
        ["MutedBrush"] = ContrastRole.WindowText,
        ["LineBrush"] = ContrastRole.WindowText,
        ["DangerBrush"] = ContrastRole.WindowText,
        ["SuccessBrush"] = ContrastRole.WindowText,
        ["StarBrush"] = ContrastRole.WindowText,
        ["AccentWarmBrush"] = ContrastRole.WindowText,
        ["BrandBrush"] = ContrastRole.Highlight,
        ["BrandDarkBrush"] = ContrastRole.Highlight,
        ["OnBrandBrush"] = ContrastRole.HighlightText,
        ["BlueBrush"] = ContrastRole.HotTrack,
    };
}

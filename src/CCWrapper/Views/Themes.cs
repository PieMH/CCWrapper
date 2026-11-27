using System.Windows;
using System.Windows.Media;

namespace CCWrapper.Views;

/// <summary>One color theme. <paramref name="Colors"/> holds a hex value for every color key of Theme.xaml; empty for
/// Default, whose colors are Theme.xaml's own. <paramref name="Light"/>: Fluent's light controls.</summary>
public sealed record Theme(string Id, string Name, bool Light, IReadOnlyDictionary<string, string> Colors);

/// <summary>The app's color themes (Settings → CCWrapper → Appearance). <see cref="Apply"/> swaps one in at start, before
/// any window exists: views read colors as StaticResource, so a change shows after a restart.</summary>
public static class Themes
{
    public const string DefaultId = "default";

    // One row per Theme.xaml color key: Dark, High contrast, Space, Sun.
    static readonly (string Key, string Dark, string Contrast, string Space, string Sun)[] Table =
    [
        ("Bg",            "#000000", "#000000", "#0B1426", "#F7F5F0"),
        ("Panel",         "#0C0C0E", "#0A0A0A", "#101C33", "#FFFFFF"),
        ("Raised",        "#17181B", "#1A1A1A", "#172642", "#EFECE5"),
        ("Hover",         "#1E2024", "#333333", "#1D2E4F", "#E8E4DB"),
        ("Line",          "#1E2024", "#8A8A8A", "#1D2E4F", "#E2DED5"),
        ("ControlLine",   "#2A2D33", "#FFFFFF", "#2A3D63", "#CFCAC0"),
        ("Text",          "#E8E8E8", "#FFFFFF", "#E8EDF7", "#1F2328"),
        ("TextSoft",      "#CFCFCF", "#FFFFFF", "#CCD5E6", "#2F353C"),
        ("Muted",         "#8C9096", "#D0D0D0", "#8D9BB8", "#626973"),
        ("TextDim",       "#B4B8BE", "#E6E6E6", "#B3BED3", "#4A5058"),
        ("TerminalBg",    "#000000", "#000000", "#081020", "#FBFAF7"),
        ("TerminalText",  "#D6D6D6", "#FFFFFF", "#D8DFEC", "#1F2328"),
        ("Pressed",       "#26292F", "#4D4D4D", "#22365C", "#DDD8CD"),
        ("RowHover",      "#121316", "#262626", "#13213B", "#F0EDE6"),
        ("MeterBack",     "#24272C", "#4D4D4D", "#22324F", "#E2DED5"),
        ("Accent",        "#4A9BE8", "#FFD400", "#F5C542", "#1F6FD1"),
        ("AccentFill",    "#2B74C2", "#FFD400", "#F5C542", "#1F6FD1"),
        ("AccentHover",   "#3381D2", "#FFE14D", "#F8D46B", "#2A7DE1"),
        ("AccentPressed", "#2566AD", "#E6BE00", "#D9A92A", "#185BB0"),
        ("OnAccent",      "#FFFFFF", "#000000", "#0B1426", "#FFFFFF"),
        ("GoodFill",      "#12261C", "#003D1C", "#12302C", "#DDF2E5"),
        ("GoodText",      "#8FDCAF", "#3DFF8B", "#8FDCAF", "#146C3A"),
        ("WarnFill",      "#2A2316", "#3D2A00", "#33291A", "#F8EBCB"),
        ("SearchMatch",   "#6B5B00", "#5C4D00", "#6B5B00", "#FFE58A"),
        ("SearchCurrent", "#C26A00", "#9A4A00", "#C26A00", "#FFB84D"),
        ("Good",          "#7FD1A0", "#3DFF8B", "#7FD1A0", "#146C3A"),
        ("Warn",          "#F2C46D", "#FFB000", "#FFB86B", "#8A5A00"),
        ("Bad",           "#F08F86", "#FF6B6B", "#F08F86", "#C0392B"),
        ("BadLine",       "#4A2C2A", "#FF6B6B", "#5A3A44", "#F0C4BF"),
        ("Selected",      "#1A1C20", "#00305C", "#1A2A4A", "#E3ECF8"),
        ("SlashText",     "#5DB0F5", "#5CC8FF", "#7CC4FF", "#0B63C4"),
        ("SwitchOff",     "#3E424A", "#9A9A9A", "#3A4B6E", "#B5AFA4"),
        ("ChangedFill",   "#0E1824", "#1A1A00", "#13233F", "#EAF1FB"),
        ("NoteFill",      "#17140E", "#1A1400", "#1F1F2A", "#FBF3E0"),
        ("NoteLine",      "#4A3D22", "#FFB000", "#5A4A2A", "#E3CB8F"),
        ("NoteText",      "#E9D7B0", "#FFE9B0", "#E9D7B0", "#5C4813"),
        ("SessionRed",    "#F06A6A", "#FF6B6B", "#F06A6A", "#C62828"),
        ("SessionBlue",   "#6A9BCC", "#6CB6FF", "#6A9BCC", "#2F6DB5"),
        ("SessionGreen",  "#16A34A", "#3DFF8B", "#16A34A", "#15803D"),
        ("SessionYellow", "#CA8A04", "#FFD400", "#CA8A04", "#A16207"),
        ("SessionPurple", "#9590CF", "#C9A0FF", "#9590CF", "#6D5BD0"),
        ("SessionOrange", "#D97757", "#FF9F5A", "#D97757", "#C2410C"),
        ("SessionPink",   "#D57A99", "#FF8AC5", "#D57A99", "#BE185D"),
        ("SessionCyan",   "#22A6C6", "#3DE0FF", "#22A6C6", "#0E7490"),
    ];

    public static readonly IReadOnlyList<Theme> All =
    [
        new(DefaultId, "Default", false, new Dictionary<string, string>()),
        new("dark", "Dark", false, Table.ToDictionary(r => r.Key, r => r.Dark)),
        new("contrast", "High contrast", false, Table.ToDictionary(r => r.Key, r => r.Contrast)),
        new("space", "Space", false, Table.ToDictionary(r => r.Key, r => r.Space)),
        new("sun", "Sun", true, Table.ToDictionary(r => r.Key, r => r.Sun)),
    ];

    /// <summary>The theme this process started with.</summary>
    public static Theme Active { get; private set; } = All[0];

    /// <summary>The theme with this id; Default for null or an unknown id.</summary>
    public static Theme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    // Fluent's own brushes, which App.xaml sets to Default's colors, take the theme's: Fluent key → Theme.xaml key.
    static readonly (string Fluent, string Key)[] FluentBrushes =
    [
        ("ApplicationBackgroundBrush", "Bg"), ("SolidBackgroundFillColorBaseBrush", "Bg"),
        ("LayerFillColorDefaultBrush", "Panel"), ("CardBackgroundFillColorDefaultBrush", "Panel"),
        ("CardStrokeColorDefaultBrush", "Line"), ("ControlFillColorDefaultBrush", "Raised"),
        ("ControlFillColorSecondaryBrush", "Hover"), ("ControlFillColorTertiaryBrush", "RowHover"),
        ("ControlFillColorInputActiveBrush", "Bg"), ("ControlStrokeColorDefaultBrush", "ControlLine"),
        ("ControlStrokeColorSecondaryBrush", "ControlLine"), ("DividerStrokeColorDefaultBrush", "Line"),
        ("SubtleFillColorSecondaryBrush", "Raised"), ("SubtleFillColorTertiaryBrush", "RowHover"),
        ("TextFillColorPrimaryBrush", "Text"), ("TextFillColorSecondaryBrush", "TextDim"),
        ("TextFillColorTertiaryBrush", "Muted"), ("AccentFillColorDefaultBrush", "AccentFill"),
        ("AccentFillColorSecondaryBrush", "AccentHover"), ("AccentFillColorTertiaryBrush", "AccentPressed"),
        ("AccentTextFillColorPrimaryBrush", "Accent"), ("TextOnAccentFillColorPrimaryBrush", "OnAccent"),
    ];

    static readonly (string Fluent, string Key)[] FluentColors =
    [
        ("SystemAccentColor", "AccentFill"), ("SystemAccentColorLight1", "AccentHover"), ("SystemAccentColorLight2", "Accent"),
        ("SystemAccentColorLight3", "Accent"), ("SystemAccentColorDark1", "AccentPressed"),
    ];

    /// <summary>Puts <paramref name="theme"/>'s colors in the app's resources. Call it once, after App.InitializeComponent and
    /// before the first window: styles and views pick their brushes up when first built.</summary>
    public static void Apply(Application app, Theme theme)
    {
        Active = theme;
        if (theme.Colors.Count == 0) return; // Default: Theme.xaml and App.xaml as they are
#pragma warning disable WPF0001 // ThemeMode is marked experimental; App.xaml sets it too
        if (theme.Light) app.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
        var palette = app.Resources.MergedDictionaries.First(d => d.Contains("Bg")); // Theme.xaml
        // Theme.xaml's styles read colors as DynamicResource: its StaticResource ones would keep the frozen brushes of its load.
        foreach (var (key, hex) in theme.Colors) palette[key] = Brush(hex);
        foreach (var (fluent, key) in FluentBrushes) app.Resources[fluent] = palette[key];
        foreach (var (fluent, key) in FluentColors) app.Resources[fluent] = ((SolidColorBrush)palette[key]).Color;
    }

    /// <summary>A theme's colors by key; Default's read from Theme.xaml itself, for the Settings swatches.</summary>
    public static IReadOnlyDictionary<string, string> ColorsOf(Theme theme) => theme.Colors.Count > 0 ? theme.Colors : DefaultColors.Value;

    static readonly Lazy<IReadOnlyDictionary<string, string>> DefaultColors = new(() =>
    {
        var xaml = new ResourceDictionary { Source = new Uri("pack://application:,,,/CCWrapper;component/Theme.xaml", UriKind.Absolute) };
        return xaml.Keys.OfType<string>().Where(k => xaml[k] is SolidColorBrush)
            .ToDictionary(k => k, k => Hex(((SolidColorBrush)xaml[k]).Color));
    });

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(ColorOf(hex));
        b.Freeze();
        return b;
    }

    /// <summary>The terminal's background and text. Claude Code paints its TUI in its own theme's colors, so the terminal is
    /// light only when that theme is; otherwise the theme's terminal colors when they are dark, else Default's (Sun's light
    /// ones would hide Claude Code's dark-theme text).</summary>
    public static (string Bg, string Text) TerminalColors(string themeBg, string themeText, bool claudeLight) =>
        claudeLight ? ("#FFFFFF", "#1F2328") : Luminance(ColorOf(themeBg)) < 0.2 ? (themeBg, themeText) : ("#101215", "#D3D9E1");

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c)
    {
        static double Lin(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG contrast ratio of two colors, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}

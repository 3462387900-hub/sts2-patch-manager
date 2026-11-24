using System.Windows;
using System.Windows.Media;

namespace SpirePatch;

/// <summary>
/// Warm Mineral Atelier (砚纸松墨) visual tokens matching design/neon-exploration/styles.css.
/// Signal-only restraint: neutral mineral paper surfaces; semantic colors appear only when earned.
/// </summary>
public static class NeonPalette
{
    public static readonly FontFamily SansFont = new("MiSans, Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Noto Serif SC, STZhongsong, Georgia, SimSun");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    // Mineral Paper & Ink Surfaces
    public static readonly SolidColorBrush BgGround = Freeze("#F1EEE8");
    public static readonly SolidColorBrush BgSurface = Freeze("#FAF8F4");
    public static readonly SolidColorBrush BgElevated = Freeze("#FFFFFF");
    public static readonly SolidColorBrush BgWell = Freeze("#E8E3DA");
    public static readonly SolidColorBrush BgHover = Freeze("#F4F0E8");
    public static readonly SolidColorBrush BgSelected = Freeze("#EBF2EE");

    // Hairlines & Borders
    public static readonly SolidColorBrush BorderSubtle = Freeze("#E3DDD2");
    public static readonly SolidColorBrush BorderMedium = Freeze("#D2CABE");
    public static readonly SolidColorBrush BorderStrong = Freeze("#B9B0A1");

    // Ink Hierarchy
    public static readonly SolidColorBrush InkPrimary = Freeze("#1B201D");
    public static readonly SolidColorBrush InkSecondary = Freeze("#4E5751");
    public static readonly SolidColorBrush InkMuted = Freeze("#7F8781");
    public static readonly SolidColorBrush InkFaint = Freeze("#A4ACA6");

    // Signal-Only Semantic Palette: Sage (Healthy / Primary Action)
    public static readonly SolidColorBrush Sage = Freeze("#245D47");
    public static readonly SolidColorBrush SageHover = Freeze("#1C4B39");
    public static readonly SolidColorBrush SageOnPrimary = Freeze("#FAFDFB");
    public static readonly SolidColorBrush SageBg = Freeze("#E7F2EC");
    public static readonly SolidColorBrush SageBorder = Freeze("#B3D5C3");
    public static readonly SolidColorBrush SageReadyBorder = Freeze("#C5DFD0");
    public static readonly SolidColorBrush SageLockedToggle = Freeze("#9DB5A9");
    public static readonly SolidColorBrush SageHalo = Freeze("#24245D47");
    public static readonly SolidColorBrush SageAlertInk = Freeze("#1A4735");
    public static readonly SolidColorBrush SageFocusRing = Freeze("#1F245D47");

    // Cinnabar (Hard Block / Danger)
    public static readonly SolidColorBrush Cinnabar = Freeze("#B83525");
    public static readonly SolidColorBrush CinnabarHover = Freeze("#992B1D");
    public static readonly SolidColorBrush CinnabarOnDanger = Freeze("#FFF9F8");
    public static readonly SolidColorBrush CinnabarBg = Freeze("#FCECE9");
    public static readonly SolidColorBrush CinnabarBorder = Freeze("#EBB5AC");
    public static readonly SolidColorBrush CinnabarHalo = Freeze("#24B83525");
    public static readonly SolidColorBrush RowDangerBg = Freeze("#FDF8F7");
    public static readonly SolidColorBrush RowDangerHover = Freeze("#FBEFEA");
    public static readonly SolidColorBrush ChipDangerBg = Freeze("#FFFDFC");
    public static readonly SolidColorBrush DiffDigitDangerBg = Freeze("#29B83525");
    public static readonly SolidColorBrush DiffDigitDangerInk = Freeze("#8F1E11");
    public static readonly SolidColorBrush AlertDangerInk = Freeze("#7E1E13");

    // Amber (Advisory / Update / Running / Stale Health)
    public static readonly SolidColorBrush Amber = Freeze("#A15009");
    public static readonly SolidColorBrush AmberBg = Freeze("#FDF2E2");
    public static readonly SolidColorBrush AmberHoverBg = Freeze("#FBE6C9");
    public static readonly SolidColorBrush AmberBorder = Freeze("#E9C695");
    public static readonly SolidColorBrush AmberHalo = Freeze("#2EE9983A");
    public static readonly SolidColorBrush AmberPulseHalo = Freeze("#33A15009");
    public static readonly SolidColorBrush ChipWarnBg = Freeze("#FFFDF9");
    public static readonly SolidColorBrush DiffDigitWarnBg = Freeze("#29A15009");
    public static readonly SolidColorBrush DiffDigitWarnInk = Freeze("#783703");
    public static readonly SolidColorBrush AlertWarnInk = Freeze("#783A04");

    // Indigo (Curated Patch Signal)
    public static readonly SolidColorBrush Indigo = Freeze("#2F4FA3");
    public static readonly SolidColorBrush IndigoBg = Freeze("#ECEFF9");
    public static readonly SolidColorBrush IndigoBorder = Freeze("#BECAEF");
    public static readonly SolidColorBrush ChipPatchBg = Freeze("#FCFDFE");

    // Floating Batch Dock & Toast
    public static readonly SolidColorBrush DarkDockBg = Freeze("#1E2521");
    public static readonly SolidColorBrush DarkDockInk = Freeze("#F4F7F5");
    public static readonly SolidColorBrush DarkDockBtnBg = Freeze("#1FFFFFFF");
    public static readonly SolidColorBrush DarkDockBtnHover = Freeze("#38FFFFFF");
    public static readonly SolidColorBrush DarkDockPrimary = Freeze("#337D61");
    public static readonly SolidColorBrush DarkDockPrimaryHover = Freeze("#3E9171");
    public static readonly SolidColorBrush DarkDockWarn = Freeze("#F6CA86");
    public static readonly SolidColorBrush ToastOkDot = Freeze("#5FD3A2");
    public static readonly SolidColorBrush ToastWarnDot = Freeze("#F5B85B");
    public static readonly SolidColorBrush ToastDangerDot = Freeze("#F1796D");

    // Overlays & Scrims
    public static readonly SolidColorBrush ModalBackdrop = Freeze("#5C141816");
    public static readonly SolidColorBrush DrawerBackdrop = Freeze("#47141816");
    public static readonly SolidColorBrush ScrollThumb = Freeze("#477F8781");
    public static readonly SolidColorBrush ScrollThumbHover = Freeze("#7A7F8781");
    public static readonly SolidColorBrush BrandGold = Freeze("#E8C27A");
    public static readonly SolidColorBrush BrandIvory = Freeze("#F7F5F0");
    public static readonly SolidColorBrush RowFlashBg = Freeze("#FCE6C4");
    public static readonly SolidColorBrush ToggleKnobBorder = Freeze("#2E1B201D");

    public static readonly LinearGradientBrush ReadyGateBg = CreateReadyGateGradient();

    static LinearGradientBrush CreateReadyGateGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#ECF5F0"), 0.0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F7FAF8"), 0.45));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FAF8F4"), 1.0));
        brush.Freeze();
        return brush;
    }

    internal static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public enum NeonTone
{
    Neutral,
    Sage,
    Amber,
    Cinnabar,
    Indigo
}

public enum NeonModCategory
{
    Lib,
    Char,
    Content,
    Util
}

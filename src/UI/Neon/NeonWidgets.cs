using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SpirePatch;

public sealed record VersionDiffParts(string[] ActualSegments, string[] ExpectedSegments, int FirstDiffIndex);

public static class NeonVersionDiff
{
    public static string StripPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        return trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V') ? trimmed[1..] : trimmed;
    }

    public static VersionDiffParts Compute(string? actual, string? expected)
    {
        string aClean = StripPrefix(actual);
        string eClean = StripPrefix(expected);
        string[] aParts = aClean.Length == 0 ? ["?"] : aClean.Split('.');
        string[] eParts = eClean.Length == 0 ? ["?"] : eClean.Split('.');
        int len = Math.Max(aParts.Length, eParts.Length);
        int firstDiff = -1;
        for (int i = 0; i < len; i++)
        {
            string aSeg = i < aParts.Length ? aParts[i] : "0";
            string eSeg = i < eParts.Length ? eParts[i] : "0";
            if (!string.Equals(aSeg, eSeg, StringComparison.OrdinalIgnoreCase))
            {
                firstDiff = i;
                break;
            }
        }
        return new VersionDiffParts(aParts, eParts, firstDiff);
    }

    public static int? CompareNumeric(string? a, string? b)
    {
        if (!TryParseNumeric(a, out var va) || !TryParseNumeric(b, out var vb)) return null;
        return va.CompareTo(vb);
    }

    static bool TryParseNumeric(string? raw, out Version version)
    {
        version = new Version(0, 0);
        string clean = StripPrefix(raw);
        if (clean.Length == 0) return false;
        if (!Version.TryParse(clean, out var parsed) || parsed is null) return false;
        version = new Version(
            Math.Max(0, parsed.Major),
            Math.Max(0, parsed.Minor),
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        return true;
    }
}

public sealed record NeonDiffLine(string Tag, string TagLabel, string PathText, string Note = "");

public sealed record NeonDiffGroup(string Heading, IReadOnlyList<NeonDiffLine> Lines);

public static class NeonWidgets
{
    public static TextBlock Text(
        string text,
        double size = 13.5,
        Brush? foreground = null,
        FontWeight? weight = null,
        bool mono = false,
        bool display = false,
        bool wrap = false,
        bool ellipsis = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = foreground ?? NeonPalette.InkPrimary,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = display ? NeonPalette.DisplayFont : mono ? NeonPalette.MonoFont : NeonPalette.SansFont,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = ellipsis ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        if (wrap) tb.LineHeight = Math.Round(size * 1.48, 1);
        return tb;
    }

    public static (Brush Bg, Brush Fg, Brush Border) ToneBrushes(NeonTone tone) => tone switch
    {
        NeonTone.Sage => (NeonPalette.SageBg, NeonPalette.Sage, NeonPalette.SageBorder),
        NeonTone.Amber => (NeonPalette.AmberBg, NeonPalette.Amber, NeonPalette.AmberBorder),
        NeonTone.Cinnabar => (NeonPalette.CinnabarBg, NeonPalette.Cinnabar, NeonPalette.CinnabarBorder),
        NeonTone.Indigo => (NeonPalette.IndigoBg, NeonPalette.Indigo, NeonPalette.IndigoBorder),
        _ => (NeonPalette.BgWell, NeonPalette.InkSecondary, NeonPalette.BorderSubtle)
    };

    static T At<T>(T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }

    static readonly BitmapImage? BrandAvatarSource = LoadBrandAvatarSource();

    static BitmapImage? LoadBrandAvatarSource()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/assets/SpirePatch-256.png", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Brand 26x26 emblem: uses the embedded SpirePatch avatar when available,
    /// falling back cleanly to the vector diamond emblem.
    /// </summary>
    public static UIElement CreateBrandMark()
    {
        if (BrandAvatarSource != null)
        {
            var img = new Image
            {
                Width = 26,
                Height = 26,
                Source = BrandAvatarSource,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        var canvas = new Canvas
        {
            Width = 28,
            Height = 28,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        canvas.Children.Add(At(new Rectangle
        {
            Width = 24,
            Height = 24,
            RadiusX = 6,
            RadiusY = 6,
            Fill = NeonPalette.Sage
        }, 2.0, 2.0));
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M14,6.5 L19.5,14 L14,21.5 L8.5,14 Z"),
            Stroke = NeonPalette.BrandIvory,
            StrokeThickness = 1.75,
            StrokeLineJoin = PenLineJoin.Round
        });
        canvas.Children.Add(At(new Ellipse
        {
            Width = 4.4,
            Height = 4.4,
            Fill = NeonPalette.BrandGold
        }, 11.8, 11.8));
        return new Viewbox
        {
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Child = canvas,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
    }

    /// <summary>
    /// Exact 6x6 cinnabar dot beside top navigation tab (.nav-signal-dot).
    /// </summary>
    public static Ellipse CreateNavSignalDot() => new()
    {
        Width = 6,
        Height = 6,
        Fill = NeonPalette.Cinnabar,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
        SnapsToDevicePixels = false,
        UseLayoutRounding = false,
        Visibility = Visibility.Collapsed
    };

    /// <summary>
    /// Exact tabular count pill beside top navigation tab (.nav-signal-count).
    /// </summary>
    public static (Border Pill, TextBlock Label) CreateNavSignalCount()
    {
        var label = new TextBlock
        {
            FontFamily = NeonPalette.MonoFont,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = NeonPalette.Amber,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 12
        };
        var pill = new Border
        {
            Background = NeonPalette.AmberBg,
            BorderBrush = NeonPalette.AmberBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(5.5, 2, 5.5, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = label,
            Visibility = Visibility.Collapsed
        };
        return (pill, label);
    }

    public static void UpdateNavSignalCount(Border pill, TextBlock label, int count, bool danger = false)
    {
        if (count <= 0)
        {
            pill.Visibility = Visibility.Collapsed;
            return;
        }
        pill.Visibility = Visibility.Visible;
        label.Text = count.ToString();
        pill.Background = danger ? NeonPalette.CinnabarBg : NeonPalette.AmberBg;
        pill.BorderBrush = danger ? NeonPalette.CinnabarBorder : NeonPalette.AmberBorder;
        label.Foreground = danger ? NeonPalette.Cinnabar : NeonPalette.Amber;
    }

    /// <summary>
    /// Exact 7x7 status lamp for game path pill & section headers (.status-lamp),
    /// with optional outer halo when game is running.
    /// </summary>
    public static (Grid Host, Ellipse Halo, Ellipse Core) CreateStatusLamp(double diameter = 7)
    {
        var host = new Grid
        {
            Width = diameter + 6,
            Height = diameter + 6,
            Margin = new Thickness(-3, -3, -3, -3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        var halo = new Ellipse
        {
            Width = diameter + 6,
            Height = diameter + 6,
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        var core = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = NeonPalette.Sage,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        host.Children.Add(halo);
        host.Children.Add(core);
        return (host, halo, core);
    }

    public static void SetStatusLamp(Ellipse halo, Ellipse core, NeonTone tone, bool showHalo = false)
    {
        core.Fill = tone switch
        {
            NeonTone.Amber => NeonPalette.Amber,
            NeonTone.Cinnabar => NeonPalette.Cinnabar,
            NeonTone.Indigo => NeonPalette.Indigo,
            NeonTone.Neutral => NeonPalette.InkMuted,
            _ => NeonPalette.Sage
        };
        halo.Fill = !showHalo ? Brushes.Transparent : tone switch
        {
            NeonTone.Amber => NeonPalette.AmberPulseHalo,
            NeonTone.Cinnabar => NeonPalette.CinnabarHalo,
            NeonTone.Sage => NeonPalette.SageHalo,
            _ => Brushes.Transparent
        };
    }

    /// <summary>
    /// Signal badge (.sig-badge): 11.5px Medium, 4px radius, 8x2 padding, 1px hairline border.
    /// </summary>
    public static FrameworkElement CreateBadge(
        string text,
        NeonTone tone,
        bool dashed = false,
        Action? onClick = null,
        string? toolTip = null,
        double maxWidth = 240,
        string? automationName = null)
    {
        var (bg, fg, border) = ToneBrushes(tone);
        var label = new TextBlock
        {
            Text = text,
            FontFamily = NeonPalette.SansFont,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = dashed ? NeonPalette.Amber : fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        FrameworkElement surface;
        if (dashed)
        {
            var grid = new Grid { MaxWidth = maxWidth };
            var dashedBorder = new Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                Stroke = NeonPalette.AmberBorder,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            var inner = new Border
            {
                Padding = new Thickness(8, 2.5, 8, 2.5),
                Child = label
            };
            grid.Children.Add(dashedBorder);
            grid.Children.Add(inner);
            if (onClick != null)
            {
                grid.MouseEnter += (_, _) => dashedBorder.Fill = NeonPalette.AmberBg;
                grid.MouseLeave += (_, _) => dashedBorder.Fill = Brushes.Transparent;
            }
            surface = grid;
        }
        else
        {
            var pill = new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                MaxWidth = maxWidth,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false,
                Child = label
            };
            surface = pill;
        }

        if (onClick == null)
        {
            if (toolTip != null) surface.ToolTip = toolTip;
            return surface;
        }

        var btn = new Button
        {
            Content = surface,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center,
            Template = CreateInteractiveBareButtonTemplate(pressScale: 0.95, hoverOpacity: 0.90)
        };
        if (automationName != null) AutomationProperties.SetName(btn, automationName);
        else AutomationProperties.SetName(btn, text);
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    /// <summary>
    /// Exact side-by-side Patch Status + Patch Type strip from design/neon-exploration/views.js L117-118:
    /// [Status Badge] + 6px gap + [独立补丁 | 原 MOD 内置修复].
    /// </summary>
    public static StackPanel CreatePatchStatusAndTypeStrip(
        string statusText,
        NeonTone statusTone,
        bool isStandalone,
        string? extraTag = null,
        NeonTone extraTone = NeonTone.Indigo,
        string? statusToolTip = null)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBadge = CreateBadge(statusText, statusTone, toolTip: statusToolTip);
        statusBadge.Margin = new Thickness(0, 0, 6, 0);
        strip.Children.Add(statusBadge);

        var typeBadge = CreateBadge(
            isStandalone ? "独立补丁" : "原 MOD 内置修复",
            NeonTone.Neutral,
            toolTip: isStandalone
                ? "独立补丁位于单独目录，支持安全停用与可逆退役。"
                : "直接修补原 MOD 文件，不可单独移出；需核对原 MOD 更新。");
        strip.Children.Add(typeBadge);

        if (!string.IsNullOrWhiteSpace(extraTag))
        {
            var extra = CreateBadge(extraTag, extraTone);
            extra.Margin = new Thickness(6, 0, 0, 0);
            strip.Children.Add(extra);
        }
        return strip;
    }

    /// <summary>
    /// Precision character-segment Version Diff Pill (.ver-diff-pill + .diff-digit).
    /// Highlights ONLY the exact differing version segment:
    /// - Dependency mismatch: 3.4.[5] → 需 ≥ 3.4.[7]
    /// - Update available (isUpdate=true): v3.4.[5] → 3.4.[7]
    /// </summary>
    public static Border CreateVersionDiffPill(
        string actualVersion,
        string requiredVersion,
        string op = "≥",
        string? prefix = null,
        bool warn = false,
        string? toolTip = null,
        bool isUpdate = false,
        string? suffix = null)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Brush fg = warn ? NeonPalette.Amber : NeonPalette.Cinnabar;
        Brush bg = warn ? NeonPalette.AmberBg : NeonPalette.CinnabarBg;
        Brush border = warn ? NeonPalette.AmberBorder : NeonPalette.CinnabarBorder;

        if (!string.IsNullOrWhiteSpace(prefix))
            panel.Children.Add(MonoRun(prefix + " ", fg));

        var diff = NeonVersionDiff.Compute(actualVersion, requiredVersion);
        panel.Children.Add(MonoRun("v", fg));
        AppendVersionSegments(panel, diff.ActualSegments, diff.FirstDiffIndex, fg, warn);
        string arrowText = isUpdate
            ? " → "
            : string.IsNullOrEmpty(op) ? " → 需 " : $" → 需 {op} ";
        panel.Children.Add(MonoRun(arrowText, fg));
        AppendVersionSegments(panel, diff.ExpectedSegments, diff.FirstDiffIndex, fg, warn);

        if (!string.IsNullOrWhiteSpace(suffix))
            panel.Children.Add(MonoRun(" " + suffix, fg));

        var pill = new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1.5, 7, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = panel,
            ToolTip = toolTip
        };
        AutomationProperties.SetName(pill,
            $"{(string.IsNullOrWhiteSpace(prefix) ? "" : prefix + " ")}v{NeonVersionDiff.StripPrefix(actualVersion)}{arrowText}{NeonVersionDiff.StripPrefix(requiredVersion)}");
        return pill;
    }

    /// <summary>
    /// Inline version diff sequence suitable for embedding inside Launch Gate fault chip text.
    /// </summary>
    public static void AppendVersionDiffInlines(
        InlineCollection inlines,
        string actualVersion,
        string requiredVersion,
        string op = "≥",
        bool warn = false,
        double fontSize = 12)
    {
        var diff = NeonVersionDiff.Compute(actualVersion, requiredVersion);
        Brush normalInk = NeonPalette.InkPrimary;

        void AddSegments(string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    inlines.Add(new Run(".") { FontFamily = NeonPalette.MonoFont, FontSize = fontSize, Foreground = normalInk });
                if (i == diff.FirstDiffIndex)
                    inlines.Add(new InlineUIContainer(CreateDiffDigitChip(parts[i], warn, fontSize)) { BaselineAlignment = BaselineAlignment.Center });
                else
                    inlines.Add(new Run(parts[i]) { FontFamily = NeonPalette.MonoFont, FontSize = fontSize, Foreground = normalInk });
            }
        }

        inlines.Add(new Run("v") { FontFamily = NeonPalette.MonoFont, FontSize = fontSize, Foreground = normalInk });
        AddSegments(diff.ActualSegments);
        inlines.Add(new Run($" → 需 {op} ") { FontFamily = NeonPalette.MonoFont, FontSize = fontSize, Foreground = normalInk });
        AddSegments(diff.ExpectedSegments);
    }

    static void AppendVersionSegments(Panel host, string[] parts, int firstDiff, Brush fg, bool warn)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) host.Children.Add(MonoRun(".", fg));
            if (i == firstDiff) host.Children.Add(CreateDiffDigitChip(parts[i], warn, 11.5));
            else host.Children.Add(MonoRun(parts[i], fg));
        }
    }

    static TextBlock MonoRun(string text, Brush fg, double size = 11.5) => new()
    {
        Text = text,
        FontFamily = NeonPalette.MonoFont,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = fg,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// Exact .diff-digit highlight box: soft tinted fill, 2px radius, deeper ink, crisp 1.5px bottom underline.
    /// </summary>
    public static Border CreateDiffDigitChip(string digit, bool warn, double fontSize = 11.5)
    {
        Brush ink = warn ? NeonPalette.DiffDigitWarnInk : NeonPalette.DiffDigitDangerInk;
        Brush bg = warn ? NeonPalette.DiffDigitWarnBg : NeonPalette.DiffDigitDangerBg;

        var grid = new Grid();
        var text = new TextBlock
        {
            Text = digit,
            FontFamily = NeonPalette.MonoFont,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var underline = new Border
        {
            Height = 1.5,
            Background = ink,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0.5),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        grid.Children.Add(text);
        grid.Children.Add(underline);

        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2.5, 0, 2.5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = grid
        };
    }

    /// <summary>
    /// 32x32 geometric category emblem matching app.js lines 17-28 & styles.css lines 1008-1031.
    /// </summary>
    public static Border CreateCategoryEmblem(NeonModCategory category, NeonTone rowTone)
    {
        var (bg, fg, border) = rowTone switch
        {
            NeonTone.Cinnabar => (NeonPalette.CinnabarBg, NeonPalette.Cinnabar, NeonPalette.CinnabarBorder),
            NeonTone.Amber => (NeonPalette.AmberBg, NeonPalette.Amber, NeonPalette.AmberBorder),
            _ => (NeonPalette.BgWell, NeonPalette.InkSecondary, NeonPalette.BorderSubtle)
        };

        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };

        System.Windows.Shapes.Path StrokePath(string d) => new()
        {
            Data = Geometry.Parse(d),
            Stroke = fg,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        switch (category)
        {
            case NeonModCategory.Lib:
                canvas.Children.Add(StrokePath("M12,2 L22,8.5 L12,15 L2,8.5 Z"));
                canvas.Children.Add(StrokePath("M2,15.5 L12,22 L22,15.5"));
                break;
            case NeonModCategory.Char:
                canvas.Children.Add(StrokePath("M12,3 L18,9 L12,21 L6,9 Z"));
                canvas.Children.Add(At(new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = fg
                }, 10.5, 8.5));
                break;
            case NeonModCategory.Content:
                canvas.Children.Add(At(new Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Stroke = fg,
                    StrokeThickness = 1.8
                }, 3.0, 3.0));
                canvas.Children.Add(StrokePath("M15.5,8.5 L13.5,13.5 L8.5,15.5 L10.5,10.5 Z"));
                break;
            default:
                canvas.Children.Add(StrokePath("M4,8 L20,8 M4,16 L20,16"));
                canvas.Children.Add(At(new Ellipse
                {
                    Width = 4.4,
                    Height = 4.4,
                    Stroke = fg,
                    StrokeThickness = 1.8,
                    Fill = bg
                }, 6.8, 5.8));
                canvas.Children.Add(At(new Ellipse
                {
                    Width = 4.4,
                    Height = 4.4,
                    Stroke = fg,
                    StrokeThickness = 1.8,
                    Fill = bg
                }, 12.8, 13.8));
                break;
        }

        string label = category switch
        {
            NeonModCategory.Lib => "前置库",
            NeonModCategory.Char => "角色",
            NeonModCategory.Content => "内容扩展",
            _ => "工具与汉化"
        };

        return new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            ToolTip = label,
            Child = new Viewbox
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Child = canvas
            }
        };
    }

    public static string ShortDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        int sep = name.IndexOf('·');
        return sep > 0 ? name[..sep].Trim() : name.Trim();
    }

    /// <summary>
    /// Silky Jelly Toggle Switch (FeelCurve jelly-toggle):
    /// press-down inward capsule elongation, locked micro-shake, pending hold, and damped-spring squash-stretch state flip.
    /// </summary>
    public static Button CreateToggleSwitch(
        bool isOn,
        bool isLocked,
        Action onClick,
        string? toolTip,
        string automationName,
        bool? previousIsOn = null,
        FrameworkElement? animHost = null,
        bool isPending = false)
    {
        bool shouldAnimateFlip = !isPending && previousIsOn.HasValue && previousIsOn.Value != isOn &&
                                 animHost != null && NeonMotion.CanAnimate(animHost);
        bool visualStartOn = shouldAnimateFlip ? previousIsOn!.Value : isOn;

        var knob = new Border
        {
            Width = NeonMotion.JellyKnobRestWidth,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.ToggleKnobBorder,
            BorderThickness = new Thickness(0.75),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        Canvas.SetTop(knob, 2.5);
        NeonMotion.SetJellyKnobRest(knob, visualStartOn);

        var trackCanvas = new Canvas
        {
            Width = 36,
            Height = 20,
            ClipToBounds = false
        };
        trackCanvas.Children.Add(knob);

        var track = new Border
        {
            Width = 36,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = isLocked ? NeonPalette.SageLockedToggle : visualStartOn ? NeonPalette.Sage : NeonPalette.BorderMedium,
            Opacity = isPending ? 0.68 : 1.0,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = trackCanvas
        };

        var btn = new Button
        {
            Content = track,
            Width = 36,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = isPending ? Cursors.Wait : Cursors.Hand,
            IsEnabled = !isPending,
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center,
            Template = CreateBareButtonTemplate()
        };
        AutomationProperties.SetName(btn, automationName);

        bool jellyPressed = false;
        btn.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (isLocked || isPending) return;
            jellyPressed = true;
            NeonMotion.ApplyJellyPress(animHost ?? btn, knob, isOn, isPressed: true, isLocked: false);
        };
        void ReleasePress()
        {
            if (!jellyPressed) return;
            jellyPressed = false;
            NeonMotion.ApplyJellyPress(animHost ?? btn, knob, isOn, isPressed: false, isLocked: isLocked);
        }
        btn.PreviewMouseLeftButtonUp += (_, _) => ReleasePress();
        btn.MouseLeave += (_, _) => ReleasePress();
        btn.LostMouseCapture += (_, _) => ReleasePress();

        btn.Click += (_, e) =>
        {
            e.Handled = true;
            if (isPending) return;
            ReleasePress();
            if (isLocked)
                NeonMotion.PlayLockedShake(animHost ?? btn, track);
            onClick();
        };

        if (isPending && animHost != null)
        {
            NeonMotion.StartSubtlePulse(animHost, track);
        }
        else if (shouldAnimateFlip)
        {
            bool played = false;
            void StartFlip()
            {
                if (played) return;
                played = true;
                NeonMotion.PlayJellyToggleFlip(animHost ?? btn, track, knob, previousIsOn!.Value, isOn, isLocked);
            }
            if (knob.IsLoaded) StartFlip();
            else knob.Loaded += (_, _) => StartFlip();
        }

        return btn;
    }

    /// <summary>
    /// Live-toggling Jelly Toggle backed by a real WPF CheckBox (preserves CheckBox automation & IsChecked contract).
    /// </summary>
    public static CheckBox CreateJellyToggleCheckBox(
        bool isChecked,
        string automationName,
        Action<bool> onToggled,
        bool? previousChecked = null,
        FrameworkElement? animHost = null)
    {
        bool shouldAnimateFlip = previousChecked.HasValue && previousChecked.Value != isChecked &&
                                 animHost != null && NeonMotion.CanAnimate(animHost);
        bool visualStartOn = shouldAnimateFlip ? previousChecked!.Value : isChecked;

        var knob = new Border
        {
            Width = NeonMotion.JellyKnobRestWidth,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.ToggleKnobBorder,
            BorderThickness = new Thickness(0.75),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        Canvas.SetTop(knob, 2.5);
        NeonMotion.SetJellyKnobRest(knob, visualStartOn);

        var trackCanvas = new Canvas { Width = 36, Height = 20, ClipToBounds = false };
        trackCanvas.Children.Add(knob);

        var track = new Border
        {
            Width = 36,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = visualStartOn ? NeonPalette.Sage : NeonPalette.BorderMedium,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = trackCanvas
        };

        var cb = new CheckBox
        {
            IsChecked = isChecked,
            Content = track,
            Width = 36,
            Height = 20,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template = CachedSwitchCheckBoxTemplate
        };
        AutomationProperties.SetName(cb, automationName);

        bool currentState = isChecked;
        bool jellyPressed = false;
        cb.PreviewMouseLeftButtonDown += (_, _) =>
        {
            jellyPressed = true;
            NeonMotion.ApplyJellyPress(animHost ?? cb, knob, currentState, isPressed: true, isLocked: false);
        };
        void ReleasePress()
        {
            if (!jellyPressed) return;
            jellyPressed = false;
            NeonMotion.ApplyJellyPress(animHost ?? cb, knob, currentState, isPressed: false, isLocked: false);
        }
        cb.MouseLeave += (_, _) => ReleasePress();
        cb.LostMouseCapture += (_, _) => ReleasePress();

        cb.Checked += (_, _) =>
        {
            jellyPressed = false;
            bool prev = currentState;
            currentState = true;
            NeonMotion.PlayJellyToggleFlip(animHost ?? cb, track, knob, prev, true, isLocked: false);
            onToggled(true);
        };
        cb.Unchecked += (_, _) =>
        {
            jellyPressed = false;
            bool prev = currentState;
            currentState = false;
            NeonMotion.PlayJellyToggleFlip(animHost ?? cb, track, knob, prev, false, isLocked: false);
            onToggled(false);
        };

        if (shouldAnimateFlip)
        {
            bool played = false;
            void StartFlip()
            {
                if (played) return;
                played = true;
                NeonMotion.PlayJellyToggleFlip(animHost ?? cb, track, knob, previousChecked!.Value, isChecked, isLocked: false);
            }
            if (knob.IsLoaded) StartFlip();
            else knob.Loaded += (_, _) => StartFlip();
        }

        return cb;
    }

    public static UIElement CreateLockIcon(string toolTip)
    {
        var canvas = new Canvas { Width = 16, Height = 16, SnapsToDevicePixels = false, UseLayoutRounding = false };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M5,7 V5.2 A3,3 0 0 1 11,5.2 V7"),
            Stroke = NeonPalette.Amber,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        canvas.Children.Add(At(new Rectangle
        {
            Width = 9,
            Height = 7,
            RadiusX = 1.8,
            RadiusY = 1.8,
            Fill = NeonPalette.AmberBg,
            Stroke = NeonPalette.Amber,
            StrokeThickness = 1.35
        }, 3.5, 7.0));
        return new Border
        {
            Width = 16,
            Height = 16,
            Background = Brushes.Transparent,
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox { Width = 13, Height = 13, Child = canvas }
        };
    }

    public static UIElement CreateSearchIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24, SnapsToDevicePixels = false, UseLayoutRounding = false };
        canvas.Children.Add(At(new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = NeonPalette.InkMuted,
            StrokeThickness = 2
        }, 4.0, 4.0));
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M20,20 L16.65,16.65"),
            Stroke = NeonPalette.InkMuted,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        return new Viewbox { Width = 14, Height = 14, Child = canvas, IsHitTestVisible = false };
    }

    public static UIElement CreateGearIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24, SnapsToDevicePixels = false, UseLayoutRounding = false };
        canvas.Children.Add(At(new Ellipse
        {
            Width = 6,
            Height = 6,
            Stroke = NeonPalette.InkSecondary,
            StrokeThickness = 1.8
        }, 9.0, 9.0));
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(
                "M19.4,15 A1.65,1.65 0 0 0 19.73,16.82 L19.79,16.88 A2,2 0 1 1 16.96,19.71 L16.9,19.65 A1.65,1.65 0 0 0 15.08,19.32 A1.65,1.65 0 0 0 14.08,20.83 V21 A2,2 0 0 1 10.08,21 V20.91 A1.65,1.65 0 0 0 9,19.4 A1.65,1.65 0 0 0 7.18,19.73 L7.12,19.79 A2,2 0 1 1 4.29,16.96 L4.35,16.9 A1.65,1.65 0 0 0 4.68,15.08 A1.65,1.65 0 0 0 3.17,14.08 H3 A2,2 0 0 1 3,10.08 H3.09 A1.65,1.65 0 0 0 4.6,9 A1.65,1.65 0 0 0 4.27,7.18 L4.21,7.12 A2,2 0 1 1 7.04,4.29 L7.1,4.35 A1.65,1.65 0 0 0 8.92,4.68 H9 A1.65,1.65 0 0 0 10,3.17 V3 A2,2 0 0 1 14,3 V3.09 A1.65,1.65 0 0 0 15,4.6 A1.65,1.65 0 0 0 16.82,4.27 L16.88,4.21 A2,2 0 1 1 19.71,7.04 L19.65,7.1 A1.65,1.65 0 0 0 19.32,8.92 V9 A1.65,1.65 0 0 0 20.83,10 H21 A2,2 0 0 1 21,14 H20.91 A1.65,1.65 0 0 0 19.4,15 Z"),
            Stroke = NeonPalette.InkSecondary,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        return new Viewbox { Width = 16, Height = 16, Child = canvas, IsHitTestVisible = false };
    }

    public static UIElement CreatePlayIcon(Brush fill)
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2.5,1.8 L10,6 L2.5,10.2 Z"),
            Fill = fill,
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
    }

    public static Border CreateAlert(NeonTone tone, UIElement child)
    {
        var (bg, fg, border) = tone switch
        {
            NeonTone.Cinnabar => (NeonPalette.CinnabarBg, NeonPalette.AlertDangerInk, NeonPalette.CinnabarBorder),
            NeonTone.Amber => (NeonPalette.AmberBg, NeonPalette.AlertWarnInk, NeonPalette.AmberBorder),
            NeonTone.Sage => (NeonPalette.SageBg, NeonPalette.SageAlertInk, NeonPalette.SageBorder),
            _ => (NeonPalette.BgWell, NeonPalette.InkSecondary, NeonPalette.BorderSubtle)
        };
        var box = new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 9, 11, 9),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            Child = child
        };
        TextElement.SetForeground(box, fg);
        return box;
    }

    public static Border CreateDiffBox(NeonDiffGroup group)
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = NeonPalette.BgGround,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(11, 7, 11, 7),
            Child = Text(group.Heading, 11.5, NeonPalette.InkSecondary, FontWeights.SemiBold)
        });

        for (int i = 0; i < group.Lines.Count; i++)
        {
            var line = group.Lines[i];
            var (tagBg, tagFg) = line.Tag switch
            {
                "add" => (NeonPalette.SageBg, NeonPalette.Sage),
                "del" => (NeonPalette.CinnabarBg, NeonPalette.Cinnabar),
                "skip" => (NeonPalette.BgWell, NeonPalette.InkMuted),
                _ => (NeonPalette.AmberBg, NeonPalette.Amber)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tag = new Border
            {
                Background = tagBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1.5, 5, 1.5),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(line.TagLabel, 10.5, tagFg, FontWeights.SemiBold, mono: true)
            };
            row.Children.Add(tag);

            var path = Text(line.PathText, 11.5, NeonPalette.InkPrimary, mono: true, wrap: true);
            path.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(path, 1);
            row.Children.Add(path);

            if (!string.IsNullOrWhiteSpace(line.Note))
            {
                var note = Text(line.Note, 11.5, NeonPalette.InkMuted);
                note.Margin = new Thickness(10, 0, 0, 0);
                note.VerticalAlignment = VerticalAlignment.Center;
                note.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(note, 2);
                row.Children.Add(note);
            }

            stack.Children.Add(new Border
            {
                BorderBrush = NeonPalette.BorderSubtle,
                BorderThickness = i < group.Lines.Count - 1 ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                Padding = new Thickness(11, 7, 11, 7),
                Child = row
            });
        }

        return new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = stack
        };
    }

    public static Button CreateStyledButton(
        FrameworkElement resourceScope,
        string label,
        string styleKey,
        Action onClick,
        string size = "md",
        string? toolTip = null)
    {
        var btn = new Button
        {
            Content = label,
            Style = (Style)resourceScope.FindResource(styleKey),
            ToolTip = toolTip
        };
        if (size == "sm")
        {
            btn.Height = 27;
            btn.Padding = new Thickness(10, 0, 10, 0);
            btn.FontSize = 12;
        }
        else if (size == "xs")
        {
            btn.Height = 24;
            btn.Padding = new Thickness(8, 0, 8, 0);
            btn.FontSize = 11.5;
        }
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    static readonly ControlTemplate CachedBareButtonTemplate = BuildBareButtonTemplate();
    static readonly Dictionary<(double Scale, double HoverOpacity), ControlTemplate> CachedInteractiveBareTemplates = new();
    static readonly ControlTemplate CachedSwitchCheckBoxTemplate = BuildSwitchCheckBoxTemplate();
    static readonly ControlTemplate CachedTextInputTemplate = BuildTextInputTemplate();

    static ControlTemplate BuildBareButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        template.Seal();
        return template;
    }

    static ControlTemplate BuildSwitchCheckBoxTemplate()
    {
        var template = new ControlTemplate(typeof(CheckBox));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        template.Seal();
        return template;
    }

    public static ControlTemplate CreateBareButtonTemplate() => CachedBareButtonTemplate;

    /// <summary>
    /// Tactile bare template with spring scale-down on press and subtle hover dim without shifting layout.
    /// </summary>
    public static ControlTemplate CreateInteractiveBareButtonTemplate(double pressScale = 0.96, double hoverOpacity = 0.90)
    {
        var key = (pressScale, hoverOpacity);
        if (CachedInteractiveBareTemplates.TryGetValue(key, out var cached))
            return cached;

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        border.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        if (hoverOpacity < 0.999)
        {
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, hoverOpacity, "Bd"));
            template.Triggers.Add(hover);
        }

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(NeonMotion.TactileScaleProperty, pressScale, "Bd"));
        template.Triggers.Add(pressed);

        template.Seal();
        CachedInteractiveBareTemplates[key] = template;
        return template;
    }

    /// <summary>
    /// Rounded mineral scanning progress bar with silky GPU horizontal sweep and reduced-motion static fallback.
    /// </summary>
    public static ProgressBar CreateMineralProgressBar(FrameworkElement animHost, double width = 228)
    {
        double pillWidth = Math.Round(width * 0.36);
        bool canAnimate = NeonMotion.CanAnimate(animHost);

        var template = new ControlTemplate(typeof(ProgressBar));
        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(Border.BackgroundProperty, NeonPalette.BgWell);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.5));
        track.SetValue(UIElement.ClipToBoundsProperty, true);
        track.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        track.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var pill = new FrameworkElementFactory(typeof(Border), "PART_Pill");
        pill.SetValue(FrameworkElement.WidthProperty, canAnimate ? pillWidth : Math.Round(width * 0.42));
        pill.SetValue(FrameworkElement.HeightProperty, 5.0);
        pill.SetValue(FrameworkElement.HorizontalAlignmentProperty, canAnimate ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        pill.SetValue(Border.BackgroundProperty, NeonPalette.Sage);
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.5));
        pill.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        pill.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);
        track.AppendChild(pill);
        template.VisualTree = track;

        var bar = new ProgressBar
        {
            Width = width,
            Height = 5,
            Margin = new Thickness(0, 2, 0, 12),
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = canAnimate,
            Value = canAnimate ? 0 : 42,
            Template = template
        };

        TranslateTransform? pillSlide = null;
        bar.Loaded += (_, _) =>
        {
            if (bar.Template?.FindName("PART_Pill", bar) is not Border pillBorder) return;
            if (!NeonMotion.CanAnimate(animHost))
            {
                pillBorder.HorizontalAlignment = HorizontalAlignment.Center;
                pillBorder.Width = Math.Round(width * 0.42);
                pillBorder.RenderTransform = Transform.Identity;
                return;
            }
            pillSlide = new TranslateTransform(-pillWidth, 0);
            pillBorder.RenderTransform = pillSlide;
            var sweep = new DoubleAnimation(-pillWidth, width, TimeSpan.FromMilliseconds(Math.Max(50.0, 1250.0 * NeonMotion.RecordingTimeScale)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever
            };
            pillSlide.BeginAnimation(TranslateTransform.XProperty, sweep);
        };
        bar.Unloaded += (_, _) =>
        {
            pillSlide?.BeginAnimation(TranslateTransform.XProperty, null);
        };

        return bar;
    }

    /// <summary>
    /// Shared secondary-page header (.page-head / .page-title / .page-subtitle / .page-actions).
    /// </summary>
    public static Grid CreatePageHead(string title, string subtitle, UIElement? rightActions)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Text(title, 20, NeonPalette.InkPrimary, FontWeights.Bold, display: true));
        var sub = Text(subtitle, 12.5, NeonPalette.InkSecondary, wrap: true);
        sub.Margin = new Thickness(0, 3, 0, 0);
        left.Children.Add(sub);
        grid.Children.Add(left);

        if (rightActions != null)
        {
            if (rightActions is FrameworkElement fe)
                fe.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(rightActions, 1);
            grid.Children.Add(rightActions);
        }
        return grid;
    }

    /// <summary>
    /// Honest boundary / scope disclaimer strip (.scope-note).
    /// </summary>
    public static Border CreateScopeNote(string iconGlyph, UIElement body, UIElement? rightControl = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (rightControl != null)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = iconGlyph == "ℹ"
            ? Text("i", 12, NeonPalette.InkSecondary, FontWeights.Bold, display: true)
            : Text(iconGlyph, 12.5, NeonPalette.InkSecondary, FontWeights.SemiBold);
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        if (rightControl != null)
        {
            if (rightControl is FrameworkElement fe)
            {
                fe.Margin = new Thickness(14, 0, 0, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(rightControl, 2);
            grid.Children.Add(rightControl);
        }

        return new Border
        {
            Background = NeonPalette.BgWell,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(0, 0, 0, 14),
            Child = grid
        };
    }

    /// <summary>
    /// Shared surface card with elevated header strip (.surface-card + .surface-head).
    /// </summary>
    public static Border CreateSurfaceCard(UIElement headLeft, UIElement? headRight, UIElement body, Thickness? margin = null)
    {
        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (headLeft is FrameworkElement leftFe)
            leftFe.VerticalAlignment = VerticalAlignment.Center;
        headGrid.Children.Add(headLeft);

        if (headRight != null)
        {
            if (headRight is FrameworkElement rightFe)
            {
                rightFe.VerticalAlignment = VerticalAlignment.Center;
                rightFe.Margin = new Thickness(12, 0, 0, 0);
            }
            Grid.SetColumn(headRight, 1);
            headGrid.Children.Add(headRight);
        }

        var headBorder = new Border
        {
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 11, 16, 11),
            Child = headGrid
        };

        var stack = new StackPanel();
        stack.Children.Add(headBorder);
        stack.Children.Add(body);

        return new Border
        {
            Background = NeonPalette.BgSurface,
            BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Margin = margin ?? new Thickness(0, 0, 0, 14),
            Child = stack
        };
    }

    /// <summary>
    /// Exact 7x7 status dot for card headers (.status-lamp).
    /// </summary>
    public static Ellipse CreateStaticLamp(NeonTone tone, double size = 7) => new()
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = false,
        UseLayoutRounding = false,
        Fill = tone switch
        {
            NeonTone.Amber => NeonPalette.Amber,
            NeonTone.Cinnabar => NeonPalette.Cinnabar,
            NeonTone.Indigo => NeonPalette.Indigo,
            NeonTone.Neutral => NeonPalette.InkMuted,
            _ => NeonPalette.Sage
        }
    };

    /// <summary>
    /// Exact 9x9 timeline dot for snapshot vault (.timeline-dot).
    /// </summary>
    public static Ellipse CreateTimelineDot(bool isAuto) => new()
    {
        Width = 9,
        Height = 9,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 4, 0, 0),
        SnapsToDevicePixels = false,
        UseLayoutRounding = false,
        Fill = isAuto ? NeonPalette.Indigo : NeonPalette.Sage
    };

    /// <summary>
    /// Crisp single-line text input matching .text-input with sage focus ring & placeholder (zero double-padding).
    /// </summary>
    public static (Grid Host, TextBox Input, TextBlock Placeholder) CreateTextInput(
        string placeholder,
        double height = 34,
        bool mono = false)
    {
        var grid = new Grid { Height = height };
        var box = new TextBox
        {
            Height = height,
            FontFamily = mono ? NeonPalette.MonoFont : NeonPalette.SansFont,
            FontSize = mono ? 12.5 : 13,
            Foreground = NeonPalette.InkPrimary,
            Background = NeonPalette.BgElevated,
            BorderBrush = NeonPalette.BorderMedium,
            BorderThickness = new Thickness(1),
            CaretBrush = NeonPalette.Sage,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(11, 0, 11, 0),
            Template = CachedTextInputTemplate
        };

        var ph = Text(placeholder, 12.5, NeonPalette.InkMuted, mono: mono, ellipsis: true);
        ph.Margin = new Thickness(12, 0, 12, 0);
        ph.VerticalAlignment = VerticalAlignment.Center;
        ph.IsHitTestVisible = false;

        void SyncPlaceholder() => ph.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => SyncPlaceholder();
        SyncPlaceholder();

        grid.Children.Add(box);
        grid.Children.Add(ph);
        return (grid, box, ph);
    }

    static ControlTemplate BuildTextInputTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var gridFactory = new FrameworkElementFactory(typeof(Grid));

        var haloFactory = new FrameworkElementFactory(typeof(Border), "Halo");
        haloFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        haloFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        haloFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        haloFactory.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        haloFactory.SetValue(UIElement.IsHitTestVisibleProperty, false);
        haloFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        haloFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);
        gridFactory.AppendChild(haloFactory);

        var fieldFactory = new FrameworkElementFactory(typeof(Border), "Field");
        fieldFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        fieldFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        fieldFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        fieldFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        fieldFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, false);
        fieldFactory.SetValue(FrameworkElement.UseLayoutRoundingProperty, false);

        var hostFactory = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        hostFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        hostFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        hostFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        fieldFactory.AppendChild(hostFactory);
        gridFactory.AppendChild(fieldFactory);
        template.VisualTree = gridFactory;

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, NeonPalette.Sage, "Field"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, NeonPalette.SageFocusRing, "Halo"));
        template.Triggers.Add(focus);
        template.Seal();

        return template;
    }
}

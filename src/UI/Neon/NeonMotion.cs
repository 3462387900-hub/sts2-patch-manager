using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SpirePatch;

/// <summary>
/// Parameterized, physics-inspired motion primitives for the Neon Workbench.
/// Strictly animates GPU-composited RenderTransform, Opacity, and non-frozen Brush colors
/// (plus isolated Canvas-hosted Jelly Toggle capsule width) with zero external layout thrash.
/// Obeys Windows animation settings (ClientAreaAnimation), High Contrast mode, and RecordingTimeScale.
/// </summary>
public static class NeonMotion
{
    // ---- Core timing & geometry tokens (FeelCurve-inspired asymmetric enter/exit) ----
    internal const int TabFadeMs = 150;
    internal const int InspectorOpenSlideMs = 160;
    internal const int InspectorOpenFadeMs = 145;
    internal const int InspectorCloseMs = 125;
    internal const double InspectorOpenOffsetX = 18.0;
    internal const double InspectorCloseOffsetX = 16.0;
    internal const double InspectorOpenStartOpacity = 0.12;
    internal const double TabFadeStartOpacity = 0.25;

    internal const int ModalOpenMs = 185;
    internal const int ModalBackdropOpenMs = 145;
    internal const int ModalCloseMs = 115;
    internal const double ModalStartScale = 0.965;
    internal const double ModalStartOffsetY = 10.0;

    internal const int DrawerOpenMs = 180;
    internal const int DrawerBackdropOpenMs = 140;
    internal const int DrawerCloseMs = 115;
    internal const double DrawerOffsetX = 28.0;

    internal const int BatchDockShowMs = 185;
    internal const int BatchDockHideMs = 115;
    internal const double BatchDockOffsetY = 16.0;

    internal const int ToastEnterMs = 185;
    internal const int ToastExitMs = 140;
    internal const double ToastEnterOffsetY = 14.0;
    internal const double ToastExitOffsetX = 18.0;
    internal const double ToastBatchLiftY = -58.0;

    internal const int IndicatorGlideMs = 225;
    internal const double IndicatorBaseWidth = 100.0;

    internal const int JellyToggleFlipMs = 310;
    internal const int JellyPressMs = 115;
    internal const int JellyReleaseMs = 200;
    internal const double JellyKnobRestWidth = 15.0;
    internal const double JellyKnobPressWidth = 19.5;
    internal const double JellyKnobFlightWidth = 21.5;
    internal const double JellyKnobLeftX = 2.5;
    internal const double JellyKnobRightX = 18.5;

    internal const int RowHoverMs = 105;
    internal const int RowFlashMs = 920;
    internal const int BadgePulseMs = 240;

    // Frozen semantic easing primitives (zero per-animation GC overhead)
    static readonly KeySpline SnappyOutSpline = FreezeSpline(0.22, 1.0, 0.36, 1.0);
    static readonly KeySpline SpringLaunchSpline = FreezeSpline(0.16, 1.0, 0.30, 1.0);
    static readonly KeySpline SpringSettleSpline = FreezeSpline(0.35, 0.0, 0.65, 1.0);
    static readonly IEasingFunction FastExitEase = FreezeEase(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    static readonly IEasingFunction SmoothExitEase = FreezeEase(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction PressDownEase = FreezeEase(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction TactileSpringEase = FreezeEase(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 });
    static readonly IEasingFunction CheckPopEase = FreezeEase(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.48 });

    static KeySpline FreezeSpline(double x1, double y1, double x2, double y2)
    {
        var s = new KeySpline(x1, y1, x2, y2);
        s.Freeze();
        return s;
    }

    static T FreezeEase<T>(T ease) where T : Freezable
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// Always 1.0 in normal execution and motion tests. Temporarily scaled only during
    /// software RenderTargetBitmap frame capture so CPU rasterization yields true 60fps
    /// samples when played back at exact 1:1 real time.
    /// </summary>
    internal static double RecordingTimeScale { get; set; } = 1.0;

    static TimeSpan ScaledMs(double ms) => TimeSpan.FromMilliseconds(Math.Max(1.0, ms * RecordingTimeScale));

    internal static bool CanAnimate(FrameworkElement host) =>
        host.IsVisible && !SystemParameters.HighContrast &&
        host.TryFindResource(SystemParameters.ClientAreaAnimationKey) is true;

    static DoubleAnimationUsingKeyFrames SplineDouble(double from, double to, double ms, KeySpline? spline = null)
    {
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(ScaledMs(ms)), spline ?? SnappyOutSpline));
        return anim;
    }

    static ColorAnimationUsingKeyFrames SplineColor(Color from, Color to, double ms, KeySpline? spline = null)
    {
        var anim = new ColorAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        anim.KeyFrames.Add(new DiscreteColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineColorKeyFrame(to, KeyTime.FromTimeSpan(ScaledMs(ms)), spline ?? SnappyOutSpline));
        return anim;
    }

    internal static TranslateTransform EnsureSlideTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } existing)
            return existing;
        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
    }

    internal static (ScaleTransform Scale, TranslateTransform Translate) EnsureScaleTranslate(
        FrameworkElement element,
        Point? origin = null)
    {
        if (origin.HasValue)
            element.RenderTransformOrigin = origin.Value;

        if (element.RenderTransform is TransformGroup { IsFrozen: false } group &&
            group.Children.Count == 2 &&
            group.Children[0] is ScaleTransform { IsFrozen: false } s &&
            group.Children[1] is TranslateTransform { IsFrozen: false } t)
        {
            return (s, t);
        }

        var scale = new ScaleTransform(1.0, 1.0);
        var translate = new TranslateTransform(0.0, 0.0);
        var created = new TransformGroup();
        created.Children.Add(scale);
        created.Children.Add(translate);
        element.RenderTransform = created;
        return (scale, translate);
    }

    internal static void ClearAnimations(FrameworkElement element)
    {
        element.Opacity = 1.0;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;

        if (element.RenderTransform is TranslateTransform { IsFrozen: false } transform)
        {
            transform.X = 0.0;
            transform.Y = 0.0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0.0;
            transform.Y = 0.0;
        }
        else if (element.RenderTransform is ScaleTransform { IsFrozen: false } scaleOnly)
        {
            scaleOnly.ScaleX = 1.0;
            scaleOnly.ScaleY = 1.0;
            scaleOnly.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scaleOnly.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scaleOnly.ScaleX = 1.0;
            scaleOnly.ScaleY = 1.0;
        }
        else if (element.RenderTransform is TransformGroup { IsFrozen: false } group)
        {
            foreach (var child in group.Children)
            {
                if (child is ScaleTransform { IsFrozen: false } s)
                {
                    s.ScaleX = 1.0;
                    s.ScaleY = 1.0;
                    s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    s.ScaleX = 1.0;
                    s.ScaleY = 1.0;
                }
                else if (child is TranslateTransform { IsFrozen: false } t)
                {
                    t.X = 0.0;
                    t.Y = 0.0;
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    t.X = 0.0;
                    t.Y = 0.0;
                }
            }
        }
    }

    // ========================================================================
    // ATTACHED PROPERTIES: TACTILE BUTTON PRESS & CHECKBOX TICK POP
    // ========================================================================

    public static readonly DependencyProperty TactileScaleProperty = DependencyProperty.RegisterAttached(
        "TactileScale",
        typeof(double),
        typeof(NeonMotion),
        new PropertyMetadata(1.0, OnTactileScaleChanged));

    public static double GetTactileScale(DependencyObject element) => (double)element.GetValue(TactileScaleProperty);
    public static void SetTactileScale(DependencyObject element, double value) => element.SetValue(TactileScaleProperty, value);

    static void OnTactileScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        double target = (double)e.NewValue;

        el.RenderTransformOrigin = new Point(0.5, 0.5);
        ScaleTransform scale;
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } existing)
            scale = existing;
        else
            el.RenderTransform = scale = new ScaleTransform(1.0, 1.0);

        if (!el.IsLoaded || !CanAnimate(el))
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        double fromX = scale.ScaleX;
        double fromY = scale.ScaleY;
        bool pressing = target < 0.999;
        double ms = pressing ? 55.0 : 165.0;
        IEasingFunction ease = pressing ? PressDownEase : TactileSpringEase;

        var ax = new DoubleAnimation(fromX, target, ScaledMs(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        var ay = new DoubleAnimation(fromY, target, ScaledMs(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        if (!pressing)
        {
            ax.Completed += (_, _) =>
            {
                if (Math.Abs(GetTactileScale(el) - 1.0) > 0.001) return;
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    public static readonly DependencyProperty CheckPopActiveProperty = DependencyProperty.RegisterAttached(
        "CheckPopActive",
        typeof(bool),
        typeof(NeonMotion),
        new PropertyMetadata(false, OnCheckPopActiveChanged));

    public static bool GetCheckPopActive(DependencyObject element) => (bool)element.GetValue(CheckPopActiveProperty);
    public static void SetCheckPopActive(DependencyObject element, bool value) => element.SetValue(CheckPopActiveProperty, value);

    static void OnCheckPopActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        bool active = (bool)e.NewValue;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        ScaleTransform scale;
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } existing)
            scale = existing;
        else
            el.RenderTransform = scale = new ScaleTransform(1.0, 1.0);

        if (!active || !el.IsLoaded || !CanAnimate(el))
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        var ax = new DoubleAnimation(0.52, 1.0, ScaledMs(175)) { EasingFunction = CheckPopEase, FillBehavior = FillBehavior.HoldEnd };
        var ay = new DoubleAnimation(0.52, 1.0, ScaledMs(175)) { EasingFunction = CheckPopEase, FillBehavior = FillBehavior.HoldEnd };
        ax.Completed += (_, _) =>
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    // ========================================================================
    // TAB CONTENT FADE & SHARED SLIDING INDICATOR (FeelCurve nav-indicator)
    // ========================================================================

    /// <summary>
    /// Subtle content-only fade-in on user-initiated tab switches.
    /// Topbar and footer remain completely stationary.
    /// </summary>
    internal static void FadeInTabContent(
        FrameworkElement host,
        FrameworkElement content,
        Func<bool> isStillCurrent)
    {
        if (!CanAnimate(host))
        {
            content.Opacity = 1.0;
            content.BeginAnimation(UIElement.OpacityProperty, null);
            return;
        }

        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Opacity = TabFadeStartOpacity;

        var fade = SplineDouble(TabFadeStartOpacity, 1.0, TabFadeMs);
        fade.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            content.Opacity = 1.0;
            content.BeginAnimation(UIElement.OpacityProperty, null);
        };
        content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// Smoothly glides a shared indicator bar/pill across tabs or segmented buttons using
    /// TranslateTransform.X + exact Width (keeping CornerRadius and 1px hairline borders 100% crisp!).
    /// Interruptible mid-flight; snaps instantaneously when animate=false or animations are disabled.
    /// </summary>
    internal static void PlaceSlidingIndicator(
        FrameworkElement host,
        FrameworkElement indicator,
        double x,
        double width,
        bool animate)
    {
        double clampedWidth = Math.Max(4.0, width);
        var translate = EnsureSlideTransform(indicator);
        double currentW = double.IsNaN(indicator.Width) || indicator.Width <= 0 ? clampedWidth : indicator.Width;

        bool run = animate && CanAnimate(host) && indicator.Visibility == Visibility.Visible;
        if (!run || (Math.Abs(translate.X - x) < 0.25 && Math.Abs(currentW - clampedWidth) < 0.25))
        {
            indicator.Width = clampedWidth;
            translate.X = x;
            indicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            indicator.Width = clampedWidth;
            translate.X = x;
            return;
        }

        double fromX = translate.X;
        double fromW = currentW;
        indicator.BeginAnimation(FrameworkElement.WidthProperty, SplineDouble(fromW, clampedWidth, IndicatorGlideMs));
        translate.BeginAnimation(TranslateTransform.XProperty, SplineDouble(fromX, x, IndicatorGlideMs));
    }

    // ========================================================================
    // CONTEXTUAL INSPECTOR OPEN / CLOSE
    // ========================================================================

    internal static void OpenInspector(
        FrameworkElement host,
        FrameworkElement panel,
        FrameworkElement? scrim,
        bool retargetFromClose,
        Func<bool> isStillCurrent)
    {
        var transform = EnsureSlideTransform(panel);

        if (!CanAnimate(host))
        {
            panel.Opacity = 1.0;
            panel.BeginAnimation(UIElement.OpacityProperty, null);
            transform.X = 0.0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            if (scrim != null && scrim.Visibility == Visibility.Visible)
            {
                scrim.Opacity = 1.0;
                scrim.BeginAnimation(UIElement.OpacityProperty, null);
            }
            return;
        }

        double fromX = retargetFromClose ? Math.Max(0.0, transform.X) : InspectorOpenOffsetX;
        double fromOpacity = retargetFromClose ? Math.Clamp(panel.Opacity, 0.0, 1.0) : InspectorOpenStartOpacity;
        double slideMs = retargetFromClose
            ? Math.Max(80.0, InspectorOpenSlideMs * Math.Clamp(fromX / InspectorOpenOffsetX, 0.45, 1.0))
            : InspectorOpenSlideMs;
        double fadeMs = retargetFromClose
            ? Math.Max(80.0, InspectorOpenFadeMs * Math.Clamp(1.0 - fromOpacity, 0.45, 1.0))
            : InspectorOpenFadeMs;

        panel.BeginAnimation(UIElement.OpacityProperty, null);
        panel.Opacity = fromOpacity;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = fromX;

        var slide = SplineDouble(fromX, 0.0, slideMs);
        var fade = SplineDouble(fromOpacity, 1.0, fadeMs);
        slide.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            transform.X = 0.0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            panel.Opacity = 1.0;
            panel.BeginAnimation(UIElement.OpacityProperty, null);
        };

        transform.BeginAnimation(TranslateTransform.XProperty, slide);
        panel.BeginAnimation(UIElement.OpacityProperty, fade);

        if (scrim != null && scrim.Visibility == Visibility.Visible)
        {
            double scrimFrom = retargetFromClose ? Math.Clamp(scrim.Opacity, 0.0, 1.0) : 0.0;
            scrim.BeginAnimation(UIElement.OpacityProperty, null);
            scrim.Opacity = scrimFrom;
            var scrimFade = SplineDouble(scrimFrom, 1.0, fadeMs);
            scrimFade.Completed += (_, _) =>
            {
                if (!isStillCurrent()) return;
                scrim.Opacity = 1.0;
                scrim.BeginAnimation(UIElement.OpacityProperty, null);
            };
            scrim.BeginAnimation(UIElement.OpacityProperty, scrimFade);
        }
    }

    internal static void CloseInspector(
        FrameworkElement host,
        FrameworkElement panel,
        FrameworkElement? scrim,
        Func<bool> isStillCurrent,
        Action onFinished)
    {
        var transform = EnsureSlideTransform(panel);

        if (!CanAnimate(host) || panel.Visibility != Visibility.Visible)
        {
            ClearAnimations(panel);
            if (scrim != null)
            {
                scrim.Opacity = 1.0;
                scrim.BeginAnimation(UIElement.OpacityProperty, null);
            }
            if (isStillCurrent()) onFinished();
            return;
        }

        double fromX = transform.X;
        double fromOpacity = Math.Clamp(panel.Opacity, 0.0, 1.0);

        panel.BeginAnimation(UIElement.OpacityProperty, null);
        panel.Opacity = fromOpacity;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = fromX;

        var slide = new DoubleAnimation(fromX, InspectorCloseOffsetX, ScaledMs(InspectorCloseMs))
        {
            EasingFunction = SmoothExitEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        var fade = new DoubleAnimation(fromOpacity, 0.0, ScaledMs(InspectorCloseMs))
        {
            EasingFunction = SmoothExitEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            ClearAnimations(panel);
            if (scrim != null)
            {
                scrim.Opacity = 1.0;
                scrim.BeginAnimation(UIElement.OpacityProperty, null);
            }
            onFinished();
        };

        transform.BeginAnimation(TranslateTransform.XProperty, slide);
        panel.BeginAnimation(UIElement.OpacityProperty, fade);

        if (scrim != null && scrim.Visibility == Visibility.Visible)
        {
            double scrimFrom = Math.Clamp(scrim.Opacity, 0.0, 1.0);
            scrim.BeginAnimation(UIElement.OpacityProperty, null);
            scrim.Opacity = scrimFrom;
            scrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(scrimFrom, 0.0, ScaledMs(InspectorCloseMs))
            {
                EasingFunction = SmoothExitEase,
                FillBehavior = FillBehavior.HoldEnd
            });
        }
    }

    // ========================================================================
    // JELLY TOGGLE SWITCH PHYSICS (FeelCurve jelly-toggle)
    // ========================================================================

    internal static void SetJellyKnobRest(Border knob, bool isOn)
    {
        double restX = isOn ? JellyKnobRightX : JellyKnobLeftX;
        var (scale, translate) = EnsureScaleTranslate(knob, new Point(0.5, 0.5));
        knob.BeginAnimation(FrameworkElement.WidthProperty, null);
        knob.Width = JellyKnobRestWidth;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1.0;
        scale.ScaleY = 1.0;
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.X = restX;
    }

    /// <summary>
    /// Press-down anticipation: elongates the jelly pill inward along the track while preserving
    /// crisp circular end caps and subtle volume-preserving vertical squish; springs back on release.
    /// </summary>
    internal static void ApplyJellyPress(
        FrameworkElement host,
        Border knob,
        bool isOn,
        bool isPressed,
        bool isLocked)
    {
        if (isLocked) return;

        double restX = isOn ? JellyKnobRightX : JellyKnobLeftX;
        var (scale, translate) = EnsureScaleTranslate(knob, new Point(0.5, 0.5));

        if (!CanAnimate(host))
        {
            SetJellyKnobRest(knob, isOn);
            return;
        }

        double currentW = double.IsNaN(knob.Width) ? JellyKnobRestWidth : knob.Width;
        double currentX = translate.X;
        double currentSy = scale.ScaleY;

        if (isPressed)
        {
            double targetW = JellyKnobPressWidth;
            double delta = targetW - JellyKnobRestWidth;
            double targetX = isOn ? (restX - delta) : restX;

            knob.BeginAnimation(FrameworkElement.WidthProperty, SplineDouble(currentW, targetW, JellyPressMs));
            translate.BeginAnimation(TranslateTransform.XProperty, SplineDouble(currentX, targetX, JellyPressMs));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, SplineDouble(currentSy, 0.91, JellyPressMs));
        }
        else
        {
            var aw = new DoubleAnimation(currentW, JellyKnobRestWidth, ScaledMs(JellyReleaseMs))
            {
                EasingFunction = TactileSpringEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            var ax = new DoubleAnimation(currentX, restX, ScaledMs(JellyReleaseMs))
            {
                EasingFunction = TactileSpringEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            var asy = new DoubleAnimation(currentSy, 1.0, ScaledMs(JellyReleaseMs))
            {
                EasingFunction = TactileSpringEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            aw.Completed += (_, _) =>
            {
                knob.Width = JellyKnobRestWidth;
                knob.BeginAnimation(FrameworkElement.WidthProperty, null);
                translate.X = restX;
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                scale.ScaleY = 1.0;
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
            knob.BeginAnimation(FrameworkElement.WidthProperty, aw);
            translate.BeginAnimation(TranslateTransform.XProperty, ax);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, asy);
        }
    }

    /// <summary>
    /// Full silky squash-and-stretch Jelly Toggle state flip (OFF <-> ON):
    /// mid-flight pill elongation -> overshoot glide -> gelatin landing squish -> damped spring settle,
    /// synced with smooth track color morphing.
    /// </summary>
    internal static void PlayJellyToggleFlip(
        FrameworkElement host,
        Border track,
        Border knob,
        bool fromOn,
        bool toOn,
        bool isLocked)
    {
        Color fromColor = (isLocked ? NeonPalette.SageLockedToggle : fromOn ? NeonPalette.Sage : NeonPalette.BorderMedium).Color;
        Color toColor = (isLocked ? NeonPalette.SageLockedToggle : toOn ? NeonPalette.Sage : NeonPalette.BorderMedium).Color;

        if (fromOn == toOn || !CanAnimate(host))
        {
            track.Background = isLocked ? NeonPalette.SageLockedToggle : toOn ? NeonPalette.Sage : NeonPalette.BorderMedium;
            SetJellyKnobRest(knob, toOn);
            return;
        }

        double fromX = fromOn ? JellyKnobRightX : JellyKnobLeftX;
        double toX = toOn ? JellyKnobRightX : JellyKnobLeftX;
        double dir = toOn ? 1.0 : -1.0;
        var (scale, translate) = EnsureScaleTranslate(knob, new Point(0.5, 0.5));

        // 1. Track color morph
        var trackBrush = new SolidColorBrush(fromColor);
        track.Background = trackBrush;
        var colorAnim = SplineColor(fromColor, toColor, 235);
        colorAnim.Completed += (_, _) =>
        {
            track.Background = isLocked ? NeonPalette.SageLockedToggle : toOn ? NeonPalette.Sage : NeonPalette.BorderMedium;
        };
        trackBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

        // 2. Knob horizontal glide with gentle overshoot & damped settle
        double midX = toOn ? (fromX + (toX - fromX) * 0.36) : (fromX + (toX - fromX) * 0.54);
        var xAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        xAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        xAnim.KeyFrames.Add(new SplineDoubleKeyFrame(midX, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.32)), SpringLaunchSpline));
        xAnim.KeyFrames.Add(new SplineDoubleKeyFrame(toX + dir * 2.0, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.58)), SpringLaunchSpline));
        xAnim.KeyFrames.Add(new SplineDoubleKeyFrame(toX - dir * 0.5, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.80)), SpringSettleSpline));
        xAnim.KeyFrames.Add(new SplineDoubleKeyFrame(toX, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs)), SnappyOutSpline));

        // 3. Knob pill width: stretch mid-flight -> hold gelatin momentum -> compact landing squish -> elastic settle
        var wAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        wAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(JellyKnobRestWidth, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        wAnim.KeyFrames.Add(new SplineDoubleKeyFrame(JellyKnobFlightWidth, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.28)), SpringLaunchSpline));
        wAnim.KeyFrames.Add(new SplineDoubleKeyFrame(20.2, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.44)), SnappyOutSpline));
        wAnim.KeyFrames.Add(new SplineDoubleKeyFrame(13.6, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.66)), SpringSettleSpline));
        wAnim.KeyFrames.Add(new SplineDoubleKeyFrame(15.5, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.84)), SpringSettleSpline));
        wAnim.KeyFrames.Add(new SplineDoubleKeyFrame(JellyKnobRestWidth, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs)), SnappyOutSpline));

        // 4. Volume-preserving vertical squash & bounce
        var syAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        syAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        syAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.28)), SpringLaunchSpline));
        syAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.44)), SnappyOutSpline));
        syAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.10, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.66)), SpringSettleSpline));
        syAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs * 0.84)), SpringSettleSpline));
        syAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(ScaledMs(JellyToggleFlipMs)), SnappyOutSpline));

        xAnim.Completed += (_, _) => SetJellyKnobRest(knob, toOn);

        knob.Width = JellyKnobRestWidth;
        translate.X = fromX;
        scale.ScaleY = 1.0;

        translate.BeginAnimation(TranslateTransform.XProperty, xAnim);
        knob.BeginAnimation(FrameworkElement.WidthProperty, wAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim);
    }

    /// <summary>
    /// Crisp micro-shake when user clicks a dependency-locked toggle switch.
    /// </summary>
    internal static void PlayLockedShake(FrameworkElement host, FrameworkElement target)
    {
        if (!CanAnimate(host)) return;
        var transform = EnsureSlideTransform(target);
        var shake = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        shake.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-2.6, KeyTime.FromTimeSpan(ScaledMs(45))));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(2.6, KeyTime.FromTimeSpan(ScaledMs(95))));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(ScaledMs(145))));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(ScaledMs(185))));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(ScaledMs(220))));
        shake.Completed += (_, _) =>
        {
            transform.X = 0.0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
        };
        transform.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    // ========================================================================
    // MODAL ENTRANCE / EXIT (FeelCurve modal-entrance)
    // ========================================================================

    internal static void OpenModal(
        FrameworkElement host,
        Border backdrop,
        FrameworkElement sheetHost,
        bool retargetFromClose,
        Func<bool> isStillCurrent)
    {
        var (scale, translate) = EnsureScaleTranslate(sheetHost, new Point(0.5, 0.5));
        Color targetScrim = NeonPalette.ModalBackdrop.Color;
        Color clearScrim = Color.FromArgb(0, targetScrim.R, targetScrim.G, targetScrim.B);

        if (!CanAnimate(host))
        {
            backdrop.Background = NeonPalette.ModalBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            return;
        }

        Color fromScrim = retargetFromClose && backdrop.Background is SolidColorBrush sb ? sb.Color : clearScrim;
        double fromSheetOp = retargetFromClose ? Math.Clamp(sheetHost.Opacity, 0.0, 1.0) : 0.25;
        double fromScale = retargetFromClose ? Math.Clamp(scale.ScaleX, ModalStartScale, 1.0) : ModalStartScale;
        double fromY = retargetFromClose ? Math.Clamp(translate.Y, 0.0, ModalStartOffsetY) : ModalStartOffsetY;

        var scrimBrush = new SolidColorBrush(fromScrim);
        backdrop.Background = scrimBrush;
        backdrop.BeginAnimation(UIElement.OpacityProperty, null);
        backdrop.Opacity = 1.0;

        sheetHost.BeginAnimation(UIElement.OpacityProperty, null);
        sheetHost.Opacity = fromSheetOp;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = fromScale;
        scale.ScaleY = fromScale;
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = fromY;

        var scrimAnim = SplineColor(fromScrim, targetScrim, ModalBackdropOpenMs);
        var sheetFade = SplineDouble(fromSheetOp, 1.0, ModalOpenMs);
        var sxAnim = SplineDouble(fromScale, 1.0, ModalOpenMs);
        var syAnim = SplineDouble(fromScale, 1.0, ModalOpenMs);
        var yAnim = SplineDouble(fromY, 0.0, ModalOpenMs);

        yAnim.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            backdrop.Background = NeonPalette.ModalBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
        };

        scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, scrimAnim);
        sheetHost.BeginAnimation(UIElement.OpacityProperty, sheetFade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sxAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, yAnim);
    }

    internal static void CloseModal(
        FrameworkElement host,
        Border backdrop,
        FrameworkElement sheetHost,
        Func<bool> isStillCurrent,
        Action onFinished)
    {
        if (!CanAnimate(host) || backdrop.Visibility != Visibility.Visible)
        {
            backdrop.Background = NeonPalette.ModalBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            if (isStillCurrent()) onFinished();
            return;
        }

        Color targetScrim = NeonPalette.ModalBackdrop.Color;
        Color clearScrim = Color.FromArgb(0, targetScrim.R, targetScrim.G, targetScrim.B);
        Color fromScrim = backdrop.Background is SolidColorBrush sb ? sb.Color : targetScrim;
        var scrimBrush = new SolidColorBrush(fromScrim);
        backdrop.Background = scrimBrush;

        var (scale, translate) = EnsureScaleTranslate(sheetHost, new Point(0.5, 0.5));
        double fromSheetOp = Math.Clamp(sheetHost.Opacity, 0.0, 1.0);
        double fromScale = scale.ScaleX;
        double fromY = translate.Y;

        var scrimFade = new ColorAnimation(fromScrim, clearScrim, ScaledMs(ModalCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sheetFade = new DoubleAnimation(fromSheetOp, 0.0, ScaledMs(ModalCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sx = new DoubleAnimation(fromScale, 0.97, ScaledMs(ModalCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sy = new DoubleAnimation(fromScale, 0.97, ScaledMs(ModalCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var y = new DoubleAnimation(fromY, 8.0, ScaledMs(ModalCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };

        sheetFade.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            backdrop.Background = NeonPalette.ModalBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            onFinished();
        };

        scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, scrimFade);
        sheetHost.BeginAnimation(UIElement.OpacityProperty, sheetFade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        translate.BeginAnimation(TranslateTransform.YProperty, y);
    }

    // ========================================================================
    // SLIDE-OVER TOOLS DRAWER OPEN / CLOSE
    // ========================================================================

    internal static void OpenDrawer(
        FrameworkElement host,
        Border backdrop,
        FrameworkElement sheetHost,
        bool retargetFromClose,
        Func<bool> isStillCurrent)
    {
        var transform = EnsureSlideTransform(sheetHost);
        Color targetScrim = NeonPalette.DrawerBackdrop.Color;
        Color clearScrim = Color.FromArgb(0, targetScrim.R, targetScrim.G, targetScrim.B);

        if (!CanAnimate(host))
        {
            backdrop.Background = NeonPalette.DrawerBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            return;
        }

        Color fromScrim = retargetFromClose && backdrop.Background is SolidColorBrush sb ? sb.Color : clearScrim;
        double fromSheetOp = retargetFromClose ? Math.Clamp(sheetHost.Opacity, 0.0, 1.0) : 0.30;
        double fromX = retargetFromClose ? Math.Max(0.0, transform.X) : DrawerOffsetX;

        var scrimBrush = new SolidColorBrush(fromScrim);
        backdrop.Background = scrimBrush;
        backdrop.BeginAnimation(UIElement.OpacityProperty, null);
        backdrop.Opacity = 1.0;

        sheetHost.BeginAnimation(UIElement.OpacityProperty, null);
        sheetHost.Opacity = fromSheetOp;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = fromX;

        var scrimAnim = SplineColor(fromScrim, targetScrim, DrawerBackdropOpenMs);
        var sheetFade = SplineDouble(fromSheetOp, 1.0, DrawerOpenMs);
        var slide = SplineDouble(fromX, 0.0, DrawerOpenMs);
        slide.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            backdrop.Background = NeonPalette.DrawerBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
        };

        scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, scrimAnim);
        sheetHost.BeginAnimation(UIElement.OpacityProperty, sheetFade);
        transform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    internal static void CloseDrawer(
        FrameworkElement host,
        Border backdrop,
        FrameworkElement sheetHost,
        Func<bool> isStillCurrent,
        Action onFinished)
    {
        if (!CanAnimate(host) || backdrop.Visibility != Visibility.Visible)
        {
            backdrop.Background = NeonPalette.DrawerBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            if (isStillCurrent()) onFinished();
            return;
        }

        Color targetScrim = NeonPalette.DrawerBackdrop.Color;
        Color clearScrim = Color.FromArgb(0, targetScrim.R, targetScrim.G, targetScrim.B);
        Color fromScrim = backdrop.Background is SolidColorBrush sb ? sb.Color : targetScrim;
        var scrimBrush = new SolidColorBrush(fromScrim);
        backdrop.Background = scrimBrush;

        var transform = EnsureSlideTransform(sheetHost);
        double fromSheetOp = Math.Clamp(sheetHost.Opacity, 0.0, 1.0);
        double fromX = transform.X;

        var scrimFade = new ColorAnimation(fromScrim, clearScrim, ScaledMs(DrawerCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sheetFade = new DoubleAnimation(fromSheetOp, 0.0, ScaledMs(DrawerCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var slide = new DoubleAnimation(fromX, DrawerOffsetX * 0.85, ScaledMs(DrawerCloseMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        sheetFade.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            backdrop.Background = NeonPalette.DrawerBackdrop;
            ClearAnimations(backdrop);
            ClearAnimations(sheetHost);
            onFinished();
        };

        scrimBrush.BeginAnimation(SolidColorBrush.ColorProperty, scrimFade);
        sheetHost.BeginAnimation(UIElement.OpacityProperty, sheetFade);
        transform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    // ========================================================================
    // FLOATING BATCH DOCK & TOAST CHOREOGRAPHY (FeelCurve toast-exit + anti-collision)
    // ========================================================================

    internal static void ShowBatchDock(
        FrameworkElement host,
        FrameworkElement dock,
        bool retargetFromHide,
        Func<bool> isStillCurrent)
    {
        var (scale, translate) = EnsureScaleTranslate(dock, new Point(0.5, 1.0));
        if (!CanAnimate(host))
        {
            ClearAnimations(dock);
            return;
        }

        double fromOp = retargetFromHide ? Math.Clamp(dock.Opacity, 0.0, 1.0) : 0.0;
        double fromY = retargetFromHide ? Math.Clamp(translate.Y, 0.0, BatchDockOffsetY) : BatchDockOffsetY;
        double fromScale = retargetFromHide ? Math.Clamp(scale.ScaleX, 0.97, 1.0) : 0.97;

        dock.BeginAnimation(UIElement.OpacityProperty, null);
        dock.Opacity = fromOp;
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = fromY;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = fromScale;
        scale.ScaleY = fromScale;

        var fade = SplineDouble(fromOp, 1.0, BatchDockShowMs);
        var slide = SplineDouble(fromY, 0.0, BatchDockShowMs);
        var sx = SplineDouble(fromScale, 1.0, BatchDockShowMs);
        var sy = SplineDouble(fromScale, 1.0, BatchDockShowMs);
        slide.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            ClearAnimations(dock);
        };

        dock.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    internal static void HideBatchDock(
        FrameworkElement host,
        FrameworkElement dock,
        Func<bool> isStillCurrent,
        Action onFinished)
    {
        if (!CanAnimate(host) || dock.Visibility != Visibility.Visible)
        {
            ClearAnimations(dock);
            if (isStillCurrent()) onFinished();
            return;
        }

        var (scale, translate) = EnsureScaleTranslate(dock, new Point(0.5, 1.0));
        var fade = new DoubleAnimation(Math.Clamp(dock.Opacity, 0.0, 1.0), 0.0, ScaledMs(BatchDockHideMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var slide = new DoubleAnimation(translate.Y, BatchDockOffsetY * 0.75, ScaledMs(BatchDockHideMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sx = new DoubleAnimation(scale.ScaleX, 0.975, ScaledMs(BatchDockHideMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sy = new DoubleAnimation(scale.ScaleY, 0.975, ScaledMs(BatchDockHideMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            ClearAnimations(dock);
            onFinished();
        };

        dock.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    internal static void OffsetToastRack(FrameworkElement host, FrameworkElement toastRack, double targetY)
    {
        var transform = EnsureSlideTransform(toastRack);
        if (Math.Abs(transform.Y - targetY) < 0.25) return;
        if (!CanAnimate(host))
        {
            transform.Y = targetY;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }
        transform.BeginAnimation(TranslateTransform.YProperty, SplineDouble(transform.Y, targetY, 180));
    }

    internal static void EnterToast(FrameworkElement host, FrameworkElement toastHost)
    {
        if (!CanAnimate(host))
        {
            ClearAnimations(toastHost);
            return;
        }
        var (scale, translate) = EnsureScaleTranslate(toastHost, new Point(0.5, 1.0));
        toastHost.Opacity = 0.0;
        translate.Y = ToastEnterOffsetY;
        scale.ScaleX = 0.96;
        scale.ScaleY = 0.96;

        var fade = SplineDouble(0.0, 1.0, ToastEnterMs);
        var slide = SplineDouble(ToastEnterOffsetY, 0.0, ToastEnterMs);
        var sx = SplineDouble(0.96, 1.0, ToastEnterMs);
        var sy = SplineDouble(0.96, 1.0, ToastEnterMs);
        slide.Completed += (_, _) => ClearAnimations(toastHost);

        toastHost.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    internal static void ExitToast(FrameworkElement host, FrameworkElement toastHost, Action onFinished)
    {
        if (!CanAnimate(host))
        {
            onFinished();
            return;
        }
        var (scale, translate) = EnsureScaleTranslate(toastHost, new Point(1.0, 0.5));
        var fade = new DoubleAnimation(Math.Clamp(toastHost.Opacity, 0.0, 1.0), 0.0, ScaledMs(ToastExitMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var slide = new DoubleAnimation(translate.X, ToastExitOffsetX, ScaledMs(ToastExitMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        var sx = new DoubleAnimation(scale.ScaleX, 0.97, ScaledMs(ToastExitMs)) { EasingFunction = FastExitEase, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => onFinished();

        toastHost.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.XProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
    }

    // ========================================================================
    // ROW HOVER, LOCATE FLASH DECAY & BADGE MICRO-PULSE
    // ========================================================================

    internal static void AnimateRowBackground(FrameworkElement host, Border row, Brush targetBrush, int ms = RowHoverMs)
    {
        if (targetBrush is not SolidColorBrush targetSolid)
        {
            row.Background = targetBrush;
            return;
        }

        if (!CanAnimate(host))
        {
            row.Background = targetBrush;
            return;
        }

        Color fromColor = row.Background is SolidColorBrush currentSolid ? currentSolid.Color : targetSolid.Color;
        if (fromColor == targetSolid.Color) return;

        SolidColorBrush mutable;
        if (row.Background is SolidColorBrush existing && !existing.IsFrozen)
            mutable = existing;
        else
            row.Background = mutable = new SolidColorBrush(fromColor);

        mutable.BeginAnimation(SolidColorBrush.ColorProperty, SplineColor(fromColor, targetSolid.Color, ms));
    }

    internal static void FlashRowHighlight(
        FrameworkElement host,
        Border row,
        Color flashColor,
        Func<Brush> getRestBrush)
    {
        if (!CanAnimate(host))
        {
            row.Background = getRestBrush();
            return;
        }

        var restBrush = getRestBrush();
        Color endColor = restBrush is SolidColorBrush sb ? sb.Color : NeonPalette.BgSurface.Color;

        var mutable = new SolidColorBrush(flashColor);
        row.Background = mutable;

        var anim = new ColorAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        anim.KeyFrames.Add(new DiscreteColorKeyFrame(flashColor, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearColorKeyFrame(flashColor, KeyTime.FromTimeSpan(ScaledMs(200))));
        anim.KeyFrames.Add(new SplineColorKeyFrame(endColor, KeyTime.FromTimeSpan(ScaledMs(RowFlashMs)), SnappyOutSpline));
        anim.Completed += (_, _) =>
        {
            var finalBrush = getRestBrush();
            if (finalBrush is SolidColorBrush fsb && row.Background is SolidColorBrush cur && !cur.IsFrozen)
            {
                cur.Color = fsb.Color;
                cur.BeginAnimation(SolidColorBrush.ColorProperty, null);
            }
            else
            {
                row.Background = finalBrush;
            }
        };
        mutable.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    internal static void PulseBadge(FrameworkElement host, FrameworkElement badge)
    {
        if (!CanAnimate(host) || badge.Visibility != Visibility.Visible) return;
        badge.RenderTransformOrigin = new Point(0.5, 0.5);
        ScaleTransform scale;
        if (badge.RenderTransform is ScaleTransform { IsFrozen: false } existing)
            scale = existing;
        else
            badge.RenderTransform = scale = new ScaleTransform(1.0, 1.0);

        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(1.13, KeyTime.FromTimeSpan(ScaledMs(BadgePulseMs * 0.38)), SpringLaunchSpline));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(ScaledMs(BadgePulseMs)), SnappyOutSpline));
        anim.Completed += (_, _) =>
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    /// <summary>
    /// Calm breathing opacity pulse for in-flight inline status pills ("正在启用…").
    /// </summary>
    internal static void StartSubtlePulse(FrameworkElement host, FrameworkElement target)
    {
        if (!CanAnimate(host))
        {
            target.Opacity = 1.0;
            return;
        }
        var pulse = new DoubleAnimation(1.0, 0.56, ScaledMs(680))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = SmoothExitEase
        };
        target.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    /// <summary>
    /// Gentle vertical settle + fade-in for in-place Inspector resolution/status cards.
    /// </summary>
    internal static void EnterInlineCard(FrameworkElement host, FrameworkElement card)
    {
        if (!CanAnimate(host))
        {
            ClearAnimations(card);
            return;
        }
        var translate = EnsureSlideTransform(card);
        card.Opacity = 0.2;
        translate.Y = 5.0;
        var fade = SplineDouble(0.2, 1.0, 175);
        var slide = SplineDouble(5.0, 0.0, 175);
        slide.Completed += (_, _) => ClearAnimations(card);
        card.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Staggered cascade entrance for freshly loaded MOD rows (first 10 rows only).
    /// </summary>
    internal static void PlayCascadeReveal(FrameworkElement host, FrameworkElement element, int index, int maxAnimated = 10)
    {
        if (index < 0 || index >= maxAnimated || !CanAnimate(host))
        {
            ClearAnimations(element);
            return;
        }
        var translate = EnsureSlideTransform(element);
        element.Opacity = 0.0;
        translate.Y = 10.0;
        var delay = ScaledMs(index * 20.0);
        var fade = SplineDouble(0.0, 1.0, 195);
        fade.BeginTime = delay;
        var slide = SplineDouble(10.0, 0.0, 195);
        slide.BeginTime = delay;
        slide.Completed += (_, _) => ClearAnimations(element);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Crisp 140ms vertical settle + fade-in on user-initiated filter, sort, or mode changes.
    /// Never called during background refreshes.
    /// </summary>
    internal static void PlayFilterSettle(FrameworkElement host, FrameworkElement container)
    {
        if (!CanAnimate(host))
        {
            ClearAnimations(container);
            return;
        }
        var translate = EnsureSlideTransform(container);
        container.Opacity = 0.42;
        translate.Y = 5.5;
        var fade = SplineDouble(0.42, 1.0, 140);
        var slide = SplineDouble(5.5, 0.0, 140);
        slide.Completed += (_, _) => ClearAnimations(container);
        container.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Floating confirmation pill ("✓ 已加载 X 个模组") that enters smoothly after scan completion and auto-dismisses.
    /// </summary>
    internal static void ShowFloatingRevealPill(
        FrameworkElement host,
        FrameworkElement pill,
        int holdMs,
        Func<int> currentGeneration,
        int myGeneration,
        Action? onHidden = null)
    {
        var (scale, translate) = EnsureScaleTranslate(pill);
        pill.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = 0.0;
        pill.Visibility = Visibility.Visible;

        bool animate = CanAnimate(host);
        if (!animate)
        {
            pill.Opacity = 1.0;
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.Y = 0.0;
        }
        else
        {
            pill.Opacity = 0.0;
            scale.ScaleX = 0.94;
            scale.ScaleY = 0.94;
            translate.Y = -10.0;

            pill.BeginAnimation(UIElement.OpacityProperty, SplineDouble(0.0, 1.0, 190));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1.0, ScaledMs(220))
            {
                EasingFunction = TactileSpringEase,
                FillBehavior = FillBehavior.HoldEnd
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1.0, ScaledMs(220))
            {
                EasingFunction = TactileSpringEase,
                FillBehavior = FillBehavior.HoldEnd
            });
            translate.BeginAnimation(TranslateTransform.YProperty, SplineDouble(-10.0, 0.0, 210));
        }

        var holdTimer = new DispatcherTimer { Interval = ScaledMs(Math.Max(200, holdMs)) };
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (myGeneration != currentGeneration()) return;
            if (!CanAnimate(host))
            {
                pill.Opacity = 1.0;
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                translate.Y = 0.0;
                pill.Visibility = Visibility.Collapsed;
                onHidden?.Invoke();
                return;
            }
            var outFade = new DoubleAnimation(pill.Opacity, 0.0, ScaledMs(210))
            {
                EasingFunction = FastExitEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            var outSlide = new DoubleAnimation(translate.Y, -8.0, ScaledMs(210))
            {
                EasingFunction = FastExitEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            outSlide.Completed += (_, _) =>
            {
                if (myGeneration != currentGeneration()) return;
                pill.BeginAnimation(UIElement.OpacityProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                pill.Opacity = 1.0;
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                translate.Y = 0.0;
                pill.Visibility = Visibility.Collapsed;
                onHidden?.Invoke();
            };
            pill.BeginAnimation(UIElement.OpacityProperty, outFade);
            translate.BeginAnimation(TranslateTransform.YProperty, outSlide);
        };
        holdTimer.Start();
    }

    /// <summary>
    /// Lightweight GPU horizontal nudge for hover CTA arrows and tactile row clicks.
    /// </summary>
    internal static void NudgeHorizontal(FrameworkElement host, FrameworkElement target, double offsetX, int ms = 120)
    {
        var translate = EnsureSlideTransform(target);
        if (!CanAnimate(host))
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = offsetX;
            return;
        }
        translate.BeginAnimation(TranslateTransform.XProperty, SplineDouble(translate.X, offsetX, ms));
    }

    /// <summary>
    /// 60fps exponential-damping wheel scrolling that automatically yields to programmatic ScrollTo* calls.
    /// </summary>
    internal static void AttachSmoothWheelScroll(ScrollViewer scrollViewer, FrameworkElement animHost)
    {
        bool renderingHooked = false;
        double targetOffset = scrollViewer.VerticalOffset;
        double lastAppliedOffset = scrollViewer.VerticalOffset;
        var frameClock = new Stopwatch();

        void StopInertia()
        {
            if (!renderingHooked) return;
            renderingHooked = false;
            CompositionTarget.Rendering -= OnRenderTick;
        }

        void OnRenderTick(object? sender, EventArgs args)
        {
            if (!CanAnimate(animHost) || !scrollViewer.IsLoaded)
            {
                StopInertia();
                return;
            }
            double maxOffset = scrollViewer.ScrollableHeight;
            targetOffset = Math.Clamp(targetOffset, 0.0, maxOffset);
            double current = scrollViewer.VerticalOffset;
            double delta = targetOffset - current;
            if (Math.Abs(delta) < 0.4)
            {
                lastAppliedOffset = targetOffset;
                scrollViewer.ScrollToVerticalOffset(targetOffset);
                StopInertia();
                return;
            }

            double dt = Math.Clamp(frameClock.Elapsed.TotalSeconds, 0.001, 0.05);
            frameClock.Restart();
            double alpha = 1.0 - Math.Exp(-16.0 * dt);
            double next = current + delta * alpha;
            if (Math.Abs(targetOffset - next) < 0.4)
                next = targetOffset;
            lastAppliedOffset = next;
            scrollViewer.ScrollToVerticalOffset(next);
        }

        scrollViewer.PreviewMouseWheel += (_, e) =>
        {
            if (!CanAnimate(animHost) || e.Delta == 0 || scrollViewer.ScrollableHeight <= 0.5)
            {
                StopInertia();
                return;
            }
            e.Handled = true;
            if (!renderingHooked)
            {
                targetOffset = scrollViewer.VerticalOffset;
                lastAppliedOffset = scrollViewer.VerticalOffset;
                frameClock.Restart();
                renderingHooked = true;
                CompositionTarget.Rendering += OnRenderTick;
            }
            targetOffset = Math.Clamp(targetOffset - e.Delta * 0.72, 0.0, scrollViewer.ScrollableHeight);
        };

        scrollViewer.ScrollChanged += (_, e) =>
        {
            if (renderingHooked && Math.Abs(e.VerticalOffset - lastAppliedOffset) > 1.5)
            {
                StopInertia();
                targetOffset = e.VerticalOffset;
                lastAppliedOffset = e.VerticalOffset;
            }
        };

        scrollViewer.Unloaded += (_, _) => StopInertia();
    }
}

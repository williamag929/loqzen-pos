using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Presentation.Desktop.Controls;

/// <summary>
/// A small light that travels around a rounded rectangle's border (with a fading tail), drawn in the
/// card's own colour. Used on KDS cards that just arrived. Only animates while <see cref="IsRunning"/>
/// is true and the control is visible — zero cost otherwise. Hit-testing is disabled so it never
/// blocks touches on the card.
/// </summary>
public sealed class BorderRunnerLight : Control
{
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<BorderRunnerLight, bool>(nameof(IsRunning));
    public static readonly StyledProperty<IBrush?> ColorProperty =
        AvaloniaProperty.Register<BorderRunnerLight, IBrush?>(nameof(Color), Brushes.LimeGreen);
    public static readonly StyledProperty<double> CornerRadiusValueProperty =
        AvaloniaProperty.Register<BorderRunnerLight, double>(nameof(CornerRadiusValue), 12);
    /// <summary>Seconds per full lap.</summary>
    public static readonly StyledProperty<double> LapSecondsProperty =
        AvaloniaProperty.Register<BorderRunnerLight, double>(nameof(LapSeconds), 2.4);

    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public IBrush? Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double CornerRadiusValue { get => GetValue(CornerRadiusValueProperty); set => SetValue(CornerRadiusValueProperty, value); }
    public double LapSeconds { get => GetValue(LapSecondsProperty); set => SetValue(LapSecondsProperty, value); }

    private const int TailSegments = 14;
    private const double TailFraction = 0.12; // tail length as fraction of the perimeter
    private TimeSpan? _start;
    private double _phase; // 0..1 position around the perimeter
    private bool _attached;

    static BorderRunnerLight()
    {
        AffectsRender<BorderRunnerLight>(IsRunningProperty, ColorProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<BorderRunnerLight>(false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRunningProperty || change.Property == IsVisibleProperty)
        {
            _start = null;
            RequestFrame();
        }
    }

    private void RequestFrame()
    {
        if (!_attached || !IsRunning || !IsEffectivelyVisible) return;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_attached || !IsRunning) return;
        _start ??= time;
        var lap = Math.Max(0.3, LapSeconds);
        _phase = (time - _start.Value).TotalSeconds / lap % 1.0;
        InvalidateVisual();
        RequestFrame();
    }

    public override void Render(DrawingContext context)
    {
        if (!IsRunning || Color is not ISolidColorBrush solid) return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        var inset = 1.0;
        var r = Math.Min(CornerRadiusValue, Math.Min(w, h) / 2 - inset);
        var perimeter = 2 * (w + h - 4 * inset) - 8 * r + 2 * Math.PI * r;

        for (var i = TailSegments; i >= 0; i--)
        {
            var t = _phase - i * (TailFraction / TailSegments);
            var p = PointAt(((t % 1) + 1) % 1 * perimeter, w, h, r, inset);
            var fade = 1.0 - (double)i / (TailSegments + 1);
            var c = solid.Color;
            var radius = 2.0 + 3.5 * fade;
            // soft glow
            context.DrawEllipse(new SolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(70 * fade), c.R, c.G, c.B)), null, p, radius * 2.2, radius * 2.2);
            // core
            context.DrawEllipse(new SolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(255 * fade), c.R, c.G, c.B)), null, p, radius, radius);
        }
    }

    /// <summary>Point at distance d along the rounded-rect perimeter, clockwise from the top-left straight edge.</summary>
    private static Point PointAt(double d, double w, double h, double r, double inset)
    {
        var left = inset; var top = inset; var right = w - inset; var bottom = h - inset;
        var topLen = right - left - 2 * r;
        var sideLen = bottom - top - 2 * r;
        var arc = Math.PI * r / 2;

        if (d < topLen) return new Point(left + r + d, top);
        d -= topLen;
        if (d < arc) return Arc(right - r, top + r, r, -90 + d / arc * 90);
        d -= arc;
        if (d < sideLen) return new Point(right, top + r + d);
        d -= sideLen;
        if (d < arc) return Arc(right - r, bottom - r, r, d / arc * 90);
        d -= arc;
        if (d < topLen) return new Point(right - r - d, bottom);
        d -= topLen;
        if (d < arc) return Arc(left + r, bottom - r, r, 90 + d / arc * 90);
        d -= arc;
        if (d < sideLen) return new Point(left, bottom - r - d);
        d -= sideLen;
        return Arc(left + r, top + r, r, 180 + Math.Min(d, arc) / arc * 90);
    }

    private static Point Arc(double cx, double cy, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}

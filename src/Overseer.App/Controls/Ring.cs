using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overseer.App.Controls;

/// <summary>A circular progress ring (used for the context window gauge).</summary>
public sealed class Ring : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Ring, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Ring, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<Ring, IBrush?>(nameof(Track));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Ring, double>(nameof(Thickness), 10);

    static Ring() => AffectsRender<Ring>(ValueProperty, StrokeProperty, TrackProperty, ThicknessProperty);

    /// <summary>0..1</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var th = Thickness;
        var r = (size - th) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Track != null) ctx.DrawEllipse(null, new Pen(Track, th), c, r, r);
        var v = Math.Clamp(Value, 0, 0.9999);
        if (v < 0.001 || Stroke == null) return;
        var a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Stroke, th, lineCap: PenLineCap.Round), geo);
    }
}

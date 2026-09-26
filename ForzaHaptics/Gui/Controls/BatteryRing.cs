using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ForzaHaptics.Gui.Controls;

/// <summary>A resolution-independent battery arc starting at twelve o'clock.</summary>
public sealed class BatteryRing : Control
{
    public static readonly StyledProperty<int?> PercentageProperty =
        AvaloniaProperty.Register<BatteryRing, int?>(nameof(Percentage));

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#343D4D"));
    private static readonly IBrush LowBrush = new SolidColorBrush(Color.Parse("#FF6B7A"));
    private static readonly IBrush MediumBrush = new SolidColorBrush(Color.Parse("#FFBE6A"));
    private static readonly IBrush HighBrush = new SolidColorBrush(Color.Parse("#63D98B"));

    static BatteryRing() => AffectsRender<BatteryRing>(PercentageProperty);

    public int? Percentage
    {
        get => GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double thickness = 4;
        double radius = Math.Max(0, Math.Min(Bounds.Width, Bounds.Height) / 2 - thickness / 2);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);
        if (Percentage is not { } value || value <= 0 || radius <= 0)
            return;

        int percent = Math.Clamp(value, 0, 100);
        var brush = percent <= 20 ? LowBrush : percent <= 50 ? MediumBrush : HighBrush;
        var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round);
        if (percent == 100)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        double angle = percent / 100d * Math.PI * 2 - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false);
            path.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}

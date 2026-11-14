using System.Windows;
using System.Windows.Media;

namespace AadcCommandCenter;

// Display geometry only. No IPC, target assignment, device commands or output state.
internal sealed class CameraMapOverlay : FrameworkElement
{
    // XT-M60 output FOV1; not the 130 x 52 degree illumination envelope (FOV2).
    // Confluence KAN-29, page 14057478, section 3.
    public double HorizontalFieldOfView { get; set; } = 120;
    public const double VerticalFieldOfView = 45;
    public Point RdrCenter { get; set; }
    public Point? FcsCenter { get; set; }
    public double MinimumRadius { get; set; }
    public double MaximumRadius { get; set; }
    public double CaptureRadius { get; set; }
    public double Heading { get; set; }

    internal static Point At(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
    }

    internal static Geometry Sector(Point center, double radius, double heading, double horizontalFov)
    {
        if (radius <= 0) return Geometry.Empty;
        if (horizontalFov >= 360) return new EllipseGeometry(center, radius, radius);
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(center, true, true);
            context.LineTo(At(center, radius, heading - horizontalFov / 2), true, false);
            context.ArcTo(At(center, radius, heading + horizontalFov / 2),
                new Size(radius, radius), 0, horizontalFov > 180, SweepDirection.Clockwise, true, false);
        }
        shape.Freeze();
        return shape;
    }

    protected override void OnRender(DrawingContext dc) => Draw(dc,
        new Rect(0, 0, ActualWidth, ActualHeight), RdrCenter, FcsCenter,
        MinimumRadius, MaximumRadius, CaptureRadius, Heading, HorizontalFieldOfView);

    internal static void Draw(DrawingContext dc, Rect bounds, Point rdr, Point? fcs,
        double minimum, double maximum, double capture, double heading, double horizontalFov)
    {
        dc.PushClip(new RectangleGeometry(bounds));
        var visible = new CombinedGeometry(GeometryCombineMode.Exclude,
            Sector(rdr, maximum, heading, horizontalFov), new EllipseGeometry(rdr, minimum, minimum));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(36, 0, 230, 138)),
            new Pen(new SolidColorBrush(Color.FromArgb(185, 0, 230, 138)), 1), visible);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(95, 239, 156, 60)),
            new Pen(new SolidColorBrush(Color.FromArgb(185, 239, 156, 60)), 1), rdr, minimum, minimum);
        var boundary = new Pen(new SolidColorBrush(Color.FromRgb(74, 179, 190)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        if (fcs is Point fcsCenter)
            dc.DrawEllipse(null, boundary, fcsCenter, capture, capture);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0, 230, 138)), 1.5),
            rdr, At(rdr, maximum, heading));
        dc.Pop();
    }
}

using System.Windows;
using System.Windows.Media;
using Harmonica.Core;
namespace Harmonica.Desktop;
public sealed class NoteOverview : FrameworkElement
{
    public Compilation? Plan { get; set; }
    public double Position { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double w = ActualWidth, h = ActualHeight; if (w <= 0 || h <= 0) return;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(202, 214, 200)), 0.5);
        for (int i = 1; i < 5; i++) dc.DrawLine(grid, new(0, h * i / 5), new(w, h * i / 5));
        if (Plan == null) return;
        var noteBrush = new SolidColorBrush(Color.FromRgb(91, 130, 101));
        foreach (var n in Plan.Notes)
        {
            double x = n.StartMs / Plan.DurationMs * w, y = h - 5 - (n.Pitch - 48) / 37d * (h - 10);
            dc.DrawRoundedRectangle(noteBrush, null, new Rect(x, y, Math.Max(1.5, n.DurationMs / Plan.DurationMs * w), 3.5), 1, 1);
        }
        if (Position > 0) { double x = Math.Clamp(Position / Plan.DurationMs, 0, 1) * w; dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(34, 60, 45)), 1.5), new(x, 0), new(x, h)); }
    }
}

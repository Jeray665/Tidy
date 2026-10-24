using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Tidy.Views;

/// <summary>Live card: title, big value, detail line and a 60-second filled sparkline. Drawn with plain shapes.</summary>
public class Sparkline : Border
{
    const int Points = 60;
    readonly Queue<double> _values = new(Enumerable.Repeat(0.0, Points));
    readonly TextBlock _title = new(), _value = new(), _detail = new();
    readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    readonly Polygon _area = new() { Opacity = 0.18 };
    readonly Canvas _canvas = new() { Height = 64, Margin = new Thickness(0, 12, 0, 0), ClipToBounds = true };

    public string Title { get => _title.Text; set => _title.Text = value; }

    public Sparkline()
    {
        Style = (Style)Application.Current.FindResource("Tile");
        _title.Style = (Style)Application.Current.FindResource("Muted");
        _value.Style = (Style)Application.Current.FindResource("BigNumber");
        _detail.Style = (Style)Application.Current.FindResource("Muted");
        _value.Text = "—";
        _line.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        _area.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => Redraw();
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_value);
        stack.Children.Add(_detail);
        stack.Children.Add(_canvas);
        Child = stack;
    }

    public void Push(double pct, string value, string detail)
    {
        _values.Dequeue();
        _values.Enqueue(Math.Clamp(pct, 0, 100));
        _value.Text = value;
        _detail.Text = detail;
        Redraw();
    }

    void Redraw()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0) return;
        var pts = new PointCollection(_values.Select((v, i) => new Point(i * w / (Points - 1), h - v / 100 * (h - 2) - 1)));
        _line.Points = pts;
        var area = new PointCollection(pts) { new Point(w, h), new Point(0, h) };
        _area.Points = area;
    }
}

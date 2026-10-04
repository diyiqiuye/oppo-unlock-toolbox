

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Ui;

public class IconBox : Control
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(IconBox),
        new FrameworkPropertyMetadata("", OnKeyChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconBox),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly Dictionary<string, Geometry> GeometryCache = new();
    private static readonly object CacheLock = new();

    private readonly Path _path = new()
    {
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    static IconBox()
    {
        ForegroundProperty.OverrideMetadata(typeof(IconBox),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2B, 0x25, 0x1D))));
    }

    public IconBox()
    {
        IsHitTestVisible = false;
        var fill = new Binding(nameof(Foreground)) { Source = this };
        _path.SetBinding(Shape.FillProperty, fill);
        AddVisualChild(_path);
        AddLogicalChild(_path);
        ApplyKey();
    }

    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((IconBox)d).ApplyKey();
    }

    private void ApplyKey()
    {
        var key = Key;
        if (string.IsNullOrEmpty(key) || !Icons.Data.TryGetValue(key, out var data))
        {
            _path.Data = null;
            return;
        }
        var geometry = GetGeometry(key, data);
        _path.Data = geometry;
    }

    private static Geometry GetGeometry(string key, string data)
    {
        lock (CacheLock)
        {
            if (GeometryCache.TryGetValue(key, out var cached))
                return cached;
            var geometry = Geometry.Parse(data);
            if (geometry is PathGeometry pathGeometry)
                pathGeometry.FillRule = FillRule.Nonzero;
            geometry.Freeze();
            GeometryCache[key] = geometry;
            return geometry;
        }
    }

    protected override int VisualChildrenCount => 1;

    protected override System.Windows.Media.Visual GetVisualChild(int index) => _path;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Size;
        _path.Measure(new Size(size, size));
        return new Size(size, size);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = Math.Min(finalSize.Width, finalSize.Height);
        var size = Math.Min(side, Size);
        _path.Arrange(new Rect((finalSize.Width - size) / 2, (finalSize.Height - size) / 2, size, size));
        return finalSize;
    }
}

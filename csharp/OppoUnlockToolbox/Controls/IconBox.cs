

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Controls;

public class IconBox : Control
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(IconBox),
        new FrameworkPropertyMetadata("", OnKeyChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconBox),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>true = 线条图标（只描边不填充），几何数据要写成轮廓路径。</summary>
    public static readonly DependencyProperty OutlinedProperty = DependencyProperty.Register(
        nameof(Outlined), typeof(bool), typeof(IconBox),
        new FrameworkPropertyMetadata(false, OnOutlinedChanged));

    /// <summary>线条图标的线宽（几何坐标系里的单位，会被 Stretch 一起缩放）。</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconBox),
        new FrameworkPropertyMetadata(2.4, OnOutlinedChanged));

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
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2B, 0x25, 0x1D)),
                FrameworkPropertyMetadataOptions.None, OnForegroundChanged));
    }

    public IconBox()
    {
        IsHitTestVisible = false;
        AddVisualChild(_path);
        AddLogicalChild(_path);
        ApplyVisual();
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

    public bool Outlined
    {
        get => (bool)GetValue(OutlinedProperty);
        set => SetValue(OutlinedProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((IconBox)d).ApplyVisual();

    private static void OnOutlinedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((IconBox)d).ApplyVisual();

    /// <summary>填充和描边二选一：描边模式下 Fill 必须让开，否则闭合路径会被填实。</summary>
    private void ApplyVisual()
    {
        if (Outlined)
        {
            _path.Fill = null;
            _path.Stroke = Foreground;
            _path.StrokeThickness = StrokeThickness;
            _path.StrokeLineJoin = PenLineJoin.Round;
            _path.StrokeStartLineCap = PenLineCap.Round;
            _path.StrokeEndLineCap = PenLineCap.Round;
        }
        else
        {
            _path.Fill = Foreground;
            _path.Stroke = null;
        }
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

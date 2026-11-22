using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace UAssetManager.ModelStudio;

public sealed class OutfitAxisWidget : FrameworkElement
{
    private const double PreferredSize = 88;
    private const double PositiveRadius = 9.25;
    private const double NegativeRadius = 6.25;
    private const double HitPadding = 2;

    private static readonly Brush TipFill = FrozenBrush(Color.FromArgb(228, 28, 49, 47));
    private static readonly Brush HoverFill = FrozenBrush(Color.FromArgb(248, 41, 68, 65));
    private static readonly Brush TextFill = FrozenBrush(Color.FromRgb(248, 251, 250));
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly AxisDefinition[] Axes =
    [
        new("X", new Vector3D(1, 0, 0), true, Color.FromRgb(239, 104, 109)),
        new("-X", new Vector3D(-1, 0, 0), false, Color.FromRgb(239, 104, 109)),
        new("Y", new Vector3D(0, 0, -1), true, Color.FromRgb(119, 220, 143)),
        new("-Y", new Vector3D(0, 0, 1), false, Color.FromRgb(119, 220, 143)),
        new("Z", new Vector3D(0, 1, 0), true, Color.FromRgb(112, 183, 253)),
        new("-Z", new Vector3D(0, -1, 0), false, Color.FromRgb(112, 183, 253)),
    ];

    private readonly AxisTip[] _tips = new AxisTip[Axes.Length];
    private readonly Dictionary<(string Label, double Dpi), FormattedText> _labels = new();
    private Vector3D _forward = new(0, 0, -1);
    private Vector3D _right = new(1, 0, 0);
    private Vector3D _up = new(0, 1, 0);
    private AxisDefinition? _hovered;

    public event Action<Vector3D>? AxisSelected;

    public OutfitAxisWidget()
    {
        Cursor = Cursors.Hand;
    }

    public void UpdateOrientation(Vector3D look, Vector3D up)
    {
        if (!TryNormalize(look, out var forward) || !TryNormalize(Vector3D.CrossProduct(forward, up), out var right))
            return;

        _forward = forward;
        _right = right;
        _up = Vector3D.CrossProduct(right, forward);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(Math.Min(PreferredSize, availableSize.Width), Math.Min(PreferredSize, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ProjectTips(out var origin))
            return;

        foreach (var tip in _tips)
        {
            var axis = tip.Axis;
            var hovered = axis == _hovered;
            var offset = tip.Endpoint - origin;
            if (offset.Length > tip.Radius)
            {
                var shaftEnd = tip.Endpoint - offset * (tip.Radius / offset.Length);
                drawingContext.DrawLine(axis.Pen, origin, shaftEnd);
            }

            var fill = hovered ? HoverFill : axis.Positive ? TipFill : null;
            drawingContext.DrawEllipse(fill, axis.Pen, tip.Endpoint, tip.Radius, tip.Radius);
            if (axis.Positive || hovered)
                DrawLabel(drawingContext, axis.Label, tip.Endpoint);
        }
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var point = hitTestParameters.HitPoint;
        return TryHitAxis(point, out _) ? new PointHitTestResult(this, point) : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!TryHitAxis(e.GetPosition(this), out var tip))
            return;

        e.Handled = true;
        AxisSelected?.Invoke(tip.Axis.Direction);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(TryHitAxis(e.GetPosition(this), out var tip) ? tip.Axis : null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(null);
    }

    private void SetHovered(AxisDefinition? axis)
    {
        if (_hovered == axis)
            return;

        _hovered = axis;
        ToolTip = axis is null ? null : StudioStrings.Format("Window.AxisSnap", "切换到 {0} 轴视角", axis.Label);
        InvalidateVisual();
    }

    private bool TryHitAxis(Point point, out AxisTip tip)
    {
        if (ProjectTips(out _))
        {
            for (var index = _tips.Length - 1; index >= 0; index--)
            {
                var candidate = _tips[index];
                var radius = candidate.Radius + HitPadding;
                if ((candidate.Endpoint - point).LengthSquared <= radius * radius)
                {
                    tip = candidate;
                    return true;
                }
            }
        }
        tip = default;
        return false;
    }

    private bool ProjectTips(out Point origin)
    {
        var size = RenderSize;
        origin = new Point(size.Width * 0.5, size.Height * 0.5);
        var length = Math.Min(size.Width, size.Height) * 0.5 - PositiveRadius - HitPadding - 2;
        if (length <= 0)
            return false;

        for (var index = 0; index < Axes.Length; index++)
        {
            var axis = Axes[index];
            var direction = axis.Direction;
            var x = origin.X + Vector3D.DotProduct(direction, _right) * length;
            var y = origin.Y - Vector3D.DotProduct(direction, _up) * length;
            var depth = -Vector3D.DotProduct(direction, _forward);
            _tips[index] = new AxisTip(axis, new Point(x, y), depth, axis.Positive ? PositiveRadius : NegativeRadius);
        }
        Array.Sort(_tips, static (left, right) => left.Depth.CompareTo(right.Depth));
        return true;
    }

    private void DrawLabel(DrawingContext context, string label, Point endpoint)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = (label, dpi);
        if (!_labels.TryGetValue(key, out var text))
        {
            var size = label.Length == 1 ? 10.5 : 8.5;
            text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, size, TextFill, dpi);
            _labels.Add(key, text);
        }
        var origin = new Point(endpoint.X - text.Width * 0.5, endpoint.Y - text.Height * 0.5);
        context.DrawText(text, origin);
    }

    private static bool TryNormalize(Vector3D vector, out Vector3D normalized)
    {
        var lengthSquared = vector.LengthSquared;
        if (!double.IsFinite(lengthSquared) || lengthSquared < 1e-12)
        {
            normalized = default;
            return false;
        }
        normalized = vector / Math.Sqrt(lengthSquared);
        return true;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateAxisPen(Color color, bool positive)
    {
        if (!positive)
            color.A = 145;

        var pen = new Pen(FrozenBrush(color), positive ? 1.75 : 1.25);
        pen.Freeze();
        return pen;
    }

    private sealed record AxisDefinition(string Label, Vector3D Direction, bool Positive, Color Color)
    {
        public Pen Pen { get; } = CreateAxisPen(Color, Positive);
    }

    private readonly record struct AxisTip(AxisDefinition Axis, Point Endpoint, double Depth, double Radius);
}

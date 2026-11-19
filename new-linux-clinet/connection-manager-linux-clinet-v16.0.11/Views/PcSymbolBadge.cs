using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ConnectionManager.Models;

namespace ConnectionManager.Views;

/// <summary>
/// Draws a PC's badge (v16.0.8): a white shape on a coloured circle, any size.
/// Used in the window header and on the Settings tab. Nothing is drawn while
/// <see cref="Symbol"/> is empty (the server has not sent one yet).
/// </summary>
public class PcSymbolBadge : Control
{
    public static readonly StyledProperty<string?> SymbolProperty =
        AvaloniaProperty.Register<PcSymbolBadge, string?>(nameof(Symbol));

    /// <summary>A thin light ring around the circle (for the blue header).</summary>
    public static readonly StyledProperty<bool> ShowRingProperty =
        AvaloniaProperty.Register<PcSymbolBadge, bool>(nameof(ShowRing));

    static PcSymbolBadge()
    {
        AffectsRender<PcSymbolBadge>(SymbolProperty, ShowRingProperty);
        SymbolProperty.Changed.AddClassHandler<PcSymbolBadge>((b, _) =>
            ToolTip.SetTip(b, string.IsNullOrEmpty(b.Symbol) ? null : DeviceSymbol.Describe(b.Symbol)));
    }

    public string? Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public bool ShowRing
    {
        get => GetValue(ShowRingProperty);
        set => SetValue(ShowRingProperty, value);
    }

    private static readonly IPen Ring = new Pen(new SolidColorBrush(Color.Parse("#B3FFFFFF")), 1.5);

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Symbol)) return;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var (colour, shape) = DeviceSymbol.Parse(Symbol);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double r = size / 2;
        context.DrawEllipse(new SolidColorBrush(colour), ShowRing ? Ring : null, centre, r - (ShowRing ? 0.75 : 0), r - (ShowRing ? 0.75 : 0));
        double scale = size / 24;
        var offset = new Point(centre.X - r, centre.Y - r);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(Brushes.White, null, shape);
    }
}

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ConnectionManager.Models;

namespace ConnectionManager.Views;

/// <summary>Colour of the small type badge on each connection row.</summary>
public class TypeToBrushConverter : IValueConverter
{
    private static readonly IBrush Rdp = new SolidColorBrush(Color.Parse("#0070C0"));
    private static readonly IBrush Web = new SolidColorBrush(Color.Parse("#0F9D8A"));
    private static readonly IBrush Putty = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush Ubuntu = new SolidColorBrush(Color.Parse("#D9531E"));
    private static readonly IBrush RustDesk = new SolidColorBrush(Color.Parse("#E67E22"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionType.RDP => Rdp,
        ConnectionType.Web => Web,
        ConnectionType.Putty => Putty,
        ConnectionType.Ubuntu => Ubuntu,
        ConnectionType.RustDesk => RustDesk,
        _ => Putty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

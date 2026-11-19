using Avalonia.Media;

namespace ConnectionManager.Models;

/// <summary>
/// A PC's badge (v16.0.8): a white shape on a coloured circle, sent by the server as
/// "shape:colour" (for example "star:teal"). The server gives every signed-in PC a
/// different one. The colours and shapes (drawn in a 24 x 24 box) are the same as the
/// server website's - keep them in step with SYMBOL_COLOURS / SYMBOL_SHAPES in code/db.py.
/// </summary>
public static class DeviceSymbol
{
    private static readonly Dictionary<string, string> Colours = new()
    {
        ["blue"] = "#0070C0", ["teal"] = "#0F8B8D", ["green"] = "#2E7D32", ["amber"] = "#B7791F",
        ["orange"] = "#DD6B20", ["red"] = "#C53030", ["purple"] = "#6B46C1", ["slate"] = "#475569",
    };

    private static readonly Dictionary<string, string> Shapes = new()
    {
        ["circle"] = "M12 7 A5 5 0 1 1 11.99 7 Z",
        ["square"] = "M7.5 7.5 H16.5 V16.5 H7.5 Z",
        ["triangle"] = "M12 6.5 L18 17 H6 Z",
        ["diamond"] = "M12 5.8 L18.2 12 L12 18.2 L5.8 12 Z",
        ["star"] = "M12 6 L13.65 10.33 L18.28 10.56 L14.66 13.47 L15.88 17.94 L12 15.4 " +
                   "L8.12 17.94 L9.34 13.47 L5.72 10.56 L10.35 10.33 Z",
        ["hexagon"] = "M18 12 L15 17.2 H9 L6 12 L9 6.8 H15 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    /// <summary>The circle colour and the shape for a symbol (unknown parts: blue / circle).</summary>
    public static (Color Colour, Geometry Shape) Parse(string? symbol)
    {
        string[] parts = (symbol ?? "").Split(':', 2);
        string shape = parts.Length > 0 && Shapes.ContainsKey(parts[0]) ? parts[0] : "circle";
        string colour = parts.Length > 1 && Colours.ContainsKey(parts[1]) ? parts[1] : "blue";
        lock (Cache)
        {
            if (!Cache.TryGetValue(shape, out var geometry))
                Cache[shape] = geometry = Geometry.Parse(Shapes[shape]);
            return (Color.Parse(Colours[colour]), geometry);
        }
    }

    /// <summary>"star:teal" -> "Teal star" (for tooltips).</summary>
    public static string Describe(string? symbol)
    {
        string[] parts = (symbol ?? "").Split(':', 2);
        if (parts.Length < 2 || parts[1].Length == 0) return "Badge";
        return char.ToUpper(parts[1][0]) + parts[1][1..] + " " + parts[0];
    }
}

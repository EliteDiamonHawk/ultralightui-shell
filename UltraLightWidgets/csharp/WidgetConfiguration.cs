using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraLightWidgets;

public sealed class WidgetConfiguration
{
    private WidgetConfiguration(IReadOnlyList<WidgetDefinition> widgets) => Widgets = widgets;

    public IReadOnlyList<WidgetDefinition> Widgets { get; }

    public static WidgetConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new WidgetConfigurationException($"Configuration file was not found: {path}");

        try
        {
            using FileStream stream = File.OpenRead(path);
            WidgetFile? file = JsonSerializer.Deserialize<WidgetFile>(stream, JsonOptions);
            if (file?.Widgets == null)
                throw new WidgetConfigurationException("Configuration must contain a 'widgets' array.");

            var widgets = new List<WidgetDefinition>(file.Widgets.Count);
            for (int index = 0; index < file.Widgets.Count; index++)
                widgets.Add(Validate(file.Widgets[index], index));

            return new WidgetConfiguration(widgets);
        }
        catch (WidgetConfigurationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new WidgetConfigurationException($"Invalid JSON: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new WidgetConfigurationException($"Could not read configuration '{path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WidgetConfigurationException($"Could not read configuration '{path}': {ex.Message}", ex);
        }
    }

    public IReadOnlyList<WallpaperWindowSpec> ResolveWindowSpecs()
    {
        Screen[] screens = Screen.AllScreens;
        Rectangle virtualBounds = SystemInformation.VirtualScreen;
        var specs = new List<WallpaperWindowSpec>(Widgets.Count);

        for (int index = 0; index < Widgets.Count; index++)
        {
            WidgetDefinition widget = Widgets[index];
            if (widget.MonitorNumber > screens.Length)
                throw new WidgetConfigurationException(
                    $"Widget {index + 1} selects monitor {widget.MonitorNumber}, but only {screens.Length} monitor(s) are available.");

            Screen screen = screens[widget.MonitorNumber - 1];
            Rectangle bounds = WidgetGeometry.Resolve(widget, screen.Bounds);
            specs.Add(new WallpaperWindowSpec(
                $"widget-{index + 1}",
                widget.Url,
                bounds,
                virtualBounds,
                widget.Transparent,
                widget.MonitorNumber));
        }

        return specs;
    }

    private static WidgetDefinition Validate(WidgetFileEntry? entry, int index)
    {
        string prefix = $"Widget {index + 1}";
        double? width = entry?.Width ?? entry?.WidthAlias;
        double? height = entry?.Height ?? entry?.HeightAlias;
        bool? posScaled = entry?.PosScaled ?? entry?.PosScaleAlias;
        bool? sizeScaled = entry?.SizeScaled ?? entry?.SizeScaleAlias;
        if (entry == null)
            throw new WidgetConfigurationException($"{prefix} must be an object.");
        if (entry.X == null || entry.Y == null || posScaled == null || sizeScaled == null || width == null ||
            height == null || entry.Monitor == null || string.IsNullOrWhiteSpace(entry.Anchor) ||
            string.IsNullOrWhiteSpace(entry.Url))
            throw new WidgetConfigurationException($"{prefix} is missing one or more required fields.");
        if (double.IsNaN(entry.X.Value) || double.IsInfinity(entry.X.Value) ||
            double.IsNaN(entry.Y.Value) || double.IsInfinity(entry.Y.Value))
            throw new WidgetConfigurationException($"{prefix} x and y must be finite numbers.");
        if (double.IsNaN(width.Value) || double.IsInfinity(width.Value) || width.Value <= 0 ||
            double.IsNaN(height.Value) || double.IsInfinity(height.Value) || height.Value <= 0)
            throw new WidgetConfigurationException($"{prefix} width and height must be positive finite numbers.");
        if (entry.Monitor.Value <= 0)
            throw new WidgetConfigurationException($"{prefix} monitor must be a positive 1-based number.");
        string normalizedAnchor = entry.Anchor.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (!Enum.TryParse(normalizedAnchor, ignoreCase: true, out WidgetAnchor anchor))
            throw new WidgetConfigurationException(
                $"{prefix} anchor '{entry.Anchor}' is invalid. Use top-left, top, top-right, left, center, right, bottom-left, bottom, or bottom-right.");

        if (posScaled.Value && (entry.X.Value < -100 || entry.X.Value > 100 || entry.Y.Value < -100 || entry.Y.Value > 100))
            throw new WidgetConfigurationException($"{prefix} pos-scaled x and y must use values between -100 and 100.");
        if (sizeScaled.Value && (width.Value > 100 || height.Value > 100))
            throw new WidgetConfigurationException($"{prefix} size-scaled width and height must use values between 0 and 100.");

        string normalizedUrl;
        try
        {
            normalizedUrl = UrlBuilder.Normalize(entry.Url);
        }
        catch (OptionException ex)
        {
            throw new WidgetConfigurationException($"{prefix} has an invalid URL: {entry.Url}", ex);
        }

        return new WidgetDefinition(
            entry.X.Value,
            entry.Y.Value,
            posScaled.Value,
            sizeScaled.Value,
            anchor,
            width.Value,
            height.Value,
            entry.Monitor.Value,
            normalizedUrl,
            entry.Transparent ?? true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class WidgetFile
    {
        public List<WidgetFileEntry>? Widgets { get; set; }
    }

    private sealed class WidgetFileEntry
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        [JsonPropertyName("pos-scaled")]
        public bool? PosScaled { get; set; }
        [JsonPropertyName("pos-scale")]
        public bool? PosScaleAlias { get; set; }
        [JsonPropertyName("size-scaled")]
        public bool? SizeScaled { get; set; }
        [JsonPropertyName("size-scale")]
        public bool? SizeScaleAlias { get; set; }
        public string? Anchor { get; set; }
        public double? Width { get; set; }
        [JsonPropertyName("w")]
        public double? WidthAlias { get; set; }
        public double? Height { get; set; }
        [JsonPropertyName("h")]
        public double? HeightAlias { get; set; }
        public int? Monitor { get; set; }
        public string? Url { get; set; }
        public bool? Transparent { get; set; }
    }
}

public sealed record WidgetDefinition(
    double X,
    double Y,
    bool PosScaled,
    bool SizeScaled,
    WidgetAnchor Anchor,
    double Width,
    double Height,
    int MonitorNumber,
    string Url,
    bool Transparent);

public enum WidgetAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

public static class WidgetGeometry
{
    public static Rectangle Resolve(WidgetDefinition widget, Rectangle monitor)
    {
        // Position and size scaling are independent so either coordinate system
        // can be selected without changing the other one.
        double positionUnitX = widget.PosScaled ? monitor.Width / 100d : 1d;
        double positionUnitY = widget.PosScaled ? monitor.Height / 100d : 1d;
        double sizeUnit = widget.SizeScaled ? Math.Min(monitor.Width, monitor.Height) / 100d : 1d;
        int width = ToDimension(widget.Width * sizeUnit);
        int height = ToDimension(widget.Height * sizeUnit);
        double offsetX = widget.X * positionUnitX;
        double offsetY = widget.Y * positionUnitY;
        Point monitorAnchor = GetMonitorAnchor(widget.Anchor, monitor);
        double placementX = (widget.PosScaled ? monitor.Left : monitorAnchor.X) + offsetX;
        double placementY = (widget.PosScaled ? monitor.Top : monitorAnchor.Y) + offsetY;
        Point widgetAnchor = GetWidgetAnchor(widget.Anchor, width, height);
        int left = checked((int)Math.Round(placementX - widgetAnchor.X, MidpointRounding.AwayFromZero));
        int top = checked((int)Math.Round(placementY - widgetAnchor.Y, MidpointRounding.AwayFromZero));
        return new Rectangle(left, top, width, height);
    }

    private static int ToDimension(double value) =>
        checked(Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero)));

    private static Point GetMonitorAnchor(WidgetAnchor anchor, Rectangle monitor) => anchor switch
    {
        WidgetAnchor.TopLeft => new Point(monitor.Left, monitor.Top),
        WidgetAnchor.Top => new Point(monitor.Left + monitor.Width / 2, monitor.Top),
        WidgetAnchor.TopRight => new Point(monitor.Right, monitor.Top),
        WidgetAnchor.Left => new Point(monitor.Left, monitor.Top + monitor.Height / 2),
        WidgetAnchor.Center => new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2),
        WidgetAnchor.Right => new Point(monitor.Right, monitor.Top + monitor.Height / 2),
        WidgetAnchor.BottomLeft => new Point(monitor.Left, monitor.Bottom),
        WidgetAnchor.Bottom => new Point(monitor.Left + monitor.Width / 2, monitor.Bottom),
        WidgetAnchor.BottomRight => new Point(monitor.Right, monitor.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };

    private static Point GetWidgetAnchor(WidgetAnchor anchor, int width, int height) => anchor switch
    {
        WidgetAnchor.TopLeft => new Point(0, 0),
        WidgetAnchor.Top => new Point(width / 2, 0),
        WidgetAnchor.TopRight => new Point(width, 0),
        WidgetAnchor.Left => new Point(0, height / 2),
        WidgetAnchor.Center => new Point(width / 2, height / 2),
        WidgetAnchor.Right => new Point(width, height / 2),
        WidgetAnchor.BottomLeft => new Point(0, height),
        WidgetAnchor.Bottom => new Point(width / 2, height),
        WidgetAnchor.BottomRight => new Point(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}

public sealed class WidgetConfigurationException : Exception
{
    public WidgetConfigurationException(string message) : base(message) { }
    public WidgetConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

public static class UrlBuilder
{
    public static string Normalize(string value)
    {
        string url = value.Trim();
        if (!url.Contains("://", StringComparison.Ordinal) &&
            !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            url = $"https://{url}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeFile))
        {
            throw new OptionException($"Invalid URL: {url}");
        }

        return uri.AbsoluteUri;
    }
}

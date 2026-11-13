using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraLightStartMenu;

public sealed class StartMenuConfiguration
{
    private StartMenuConfiguration(StartMenuDefinition startMenu) => StartMenu = startMenu;

    public StartMenuDefinition StartMenu { get; }

    public static StartMenuConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new StartMenuConfigurationException($"Configuration file was not found: {path}");

        try
        {
            using FileStream stream = File.OpenRead(path);
            ConfigurationFile? file = JsonSerializer.Deserialize<ConfigurationFile>(stream, JsonOptions);
            if (file?.StartMenu == null)
                throw new StartMenuConfigurationException("Configuration must contain a 'start-menu' object.");

            return new StartMenuConfiguration(Validate(file.StartMenu));
        }
        catch (StartMenuConfigurationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new StartMenuConfigurationException($"Invalid JSON: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new StartMenuConfigurationException($"Could not read configuration '{path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StartMenuConfigurationException($"Could not read configuration '{path}': {ex.Message}", ex);
        }
    }

    public StartMenuWindowSpec ResolveWindowSpec()
    {
        Screen[] screens = Screen.AllScreens;
        if (StartMenu.MonitorNumber > screens.Length)
            throw new StartMenuConfigurationException(
                $"Start menu selects monitor {StartMenu.MonitorNumber}, but only {screens.Length} monitor(s) are available.");

        Screen screen = screens[StartMenu.MonitorNumber - 1];
        Rectangle bounds = StartMenuGeometry.Resolve(StartMenu, screen.Bounds);
        return new StartMenuWindowSpec(
            "start-menu",
            StartMenu.Url,
            bounds,
            StartMenu.Transparent,
            StartMenu.AutoHide,
            StartMenu.MonitorNumber);
    }

    private static StartMenuDefinition Validate(StartMenuFileEntry? entry)
    {
        const string prefix = "Start menu";
        double? width = entry?.Width ?? entry?.WidthAlias;
        double? height = entry?.Height ?? entry?.HeightAlias;
        bool? posScaled = entry?.PosScaled ?? entry?.PosScaleAlias;
        bool? sizeScaled = entry?.SizeScaled ?? entry?.SizeScaleAlias;
        if (entry == null)
            throw new StartMenuConfigurationException($"{prefix} must be an object.");
        if (entry.X == null || entry.Y == null || posScaled == null || sizeScaled == null || width == null ||
            height == null || entry.Monitor == null || string.IsNullOrWhiteSpace(entry.Anchor) ||
            string.IsNullOrWhiteSpace(entry.Url))
            throw new StartMenuConfigurationException($"{prefix} is missing one or more required fields.");
        if (double.IsNaN(entry.X.Value) || double.IsInfinity(entry.X.Value) ||
            double.IsNaN(entry.Y.Value) || double.IsInfinity(entry.Y.Value))
            throw new StartMenuConfigurationException($"{prefix} x and y must be finite numbers.");
        if (double.IsNaN(width.Value) || double.IsInfinity(width.Value) || width.Value <= 0 ||
            double.IsNaN(height.Value) || double.IsInfinity(height.Value) || height.Value <= 0)
            throw new StartMenuConfigurationException($"{prefix} width and height must be positive finite numbers.");
        if (entry.Monitor.Value <= 0)
            throw new StartMenuConfigurationException($"{prefix} monitor must be a positive 1-based number.");

        string normalizedAnchor = entry.Anchor.Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        if (!Enum.TryParse(normalizedAnchor, ignoreCase: true, out StartMenuAnchor anchor))
            throw new StartMenuConfigurationException(
                $"{prefix} anchor '{entry.Anchor}' is invalid. Use top-left, top, top-right, left, center, right, bottom-left, bottom, or bottom-right.");

        if (posScaled.Value && (entry.X.Value < -100 || entry.X.Value > 100 || entry.Y.Value < -100 || entry.Y.Value > 100))
            throw new StartMenuConfigurationException($"{prefix} pos-scaled x and y must use values between -100 and 100.");
        if (sizeScaled.Value && (width.Value > 100 || height.Value > 100))
            throw new StartMenuConfigurationException($"{prefix} size-scaled width and height must use values between 0 and 100.");

        string normalizedUrl;
        try
        {
            normalizedUrl = UrlBuilder.Normalize(entry.Url);
        }
        catch (OptionException ex)
        {
            throw new StartMenuConfigurationException($"{prefix} has an invalid URL: {entry.Url}", ex);
        }

        return new StartMenuDefinition(
            entry.X.Value,
            entry.Y.Value,
            posScaled.Value,
            sizeScaled.Value,
            anchor,
            width.Value,
            height.Value,
            entry.Monitor.Value,
            normalizedUrl,
            entry.Transparent ?? true,
            entry.AutoHide ?? true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class ConfigurationFile
    {
        [JsonPropertyName("start-menu")]
        public StartMenuFileEntry? StartMenu { get; set; }
    }

    private sealed class StartMenuFileEntry
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
        [JsonPropertyName("auto-hide")]
        public bool? AutoHide { get; set; }
    }
}

public sealed record StartMenuDefinition(
    double X,
    double Y,
    bool PosScaled,
    bool SizeScaled,
    StartMenuAnchor Anchor,
    double Width,
    double Height,
    int MonitorNumber,
    string Url,
    bool Transparent,
    bool AutoHide);

public enum StartMenuAnchor
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

public static class StartMenuGeometry
{
    public static Rectangle Resolve(StartMenuDefinition startMenu, Rectangle monitor)
    {
        double positionUnitX = startMenu.PosScaled ? monitor.Width / 100d : 1d;
        double positionUnitY = startMenu.PosScaled ? monitor.Height / 100d : 1d;
        double sizeUnit = startMenu.SizeScaled ? Math.Min(monitor.Width, monitor.Height) / 100d : 1d;
        int width = ToDimension(startMenu.Width * sizeUnit);
        int height = ToDimension(startMenu.Height * sizeUnit);
        double offsetX = startMenu.X * positionUnitX;
        double offsetY = startMenu.Y * positionUnitY;
        Point monitorAnchor = GetMonitorAnchor(startMenu.Anchor, monitor);
        double placementX = (startMenu.PosScaled ? monitor.Left : monitorAnchor.X) + offsetX;
        double placementY = (startMenu.PosScaled ? monitor.Top : monitorAnchor.Y) + offsetY;
        Point startMenuAnchor = GetStartMenuAnchor(startMenu.Anchor, width, height);
        int left = checked((int)Math.Round(placementX - startMenuAnchor.X, MidpointRounding.AwayFromZero));
        int top = checked((int)Math.Round(placementY - startMenuAnchor.Y, MidpointRounding.AwayFromZero));
        return new Rectangle(left, top, width, height);
    }

    private static int ToDimension(double value) =>
        checked(Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero)));

    private static Point GetMonitorAnchor(StartMenuAnchor anchor, Rectangle monitor) => anchor switch
    {
        StartMenuAnchor.TopLeft => new Point(monitor.Left, monitor.Top),
        StartMenuAnchor.Top => new Point(monitor.Left + monitor.Width / 2, monitor.Top),
        StartMenuAnchor.TopRight => new Point(monitor.Right, monitor.Top),
        StartMenuAnchor.Left => new Point(monitor.Left, monitor.Top + monitor.Height / 2),
        StartMenuAnchor.Center => new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2),
        StartMenuAnchor.Right => new Point(monitor.Right, monitor.Top + monitor.Height / 2),
        StartMenuAnchor.BottomLeft => new Point(monitor.Left, monitor.Bottom),
        StartMenuAnchor.Bottom => new Point(monitor.Left + monitor.Width / 2, monitor.Bottom),
        StartMenuAnchor.BottomRight => new Point(monitor.Right, monitor.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };

    private static Point GetStartMenuAnchor(StartMenuAnchor anchor, int width, int height) => anchor switch
    {
        StartMenuAnchor.TopLeft => new Point(0, 0),
        StartMenuAnchor.Top => new Point(width / 2, 0),
        StartMenuAnchor.TopRight => new Point(width, 0),
        StartMenuAnchor.Left => new Point(0, height / 2),
        StartMenuAnchor.Center => new Point(width / 2, height / 2),
        StartMenuAnchor.Right => new Point(width, height / 2),
        StartMenuAnchor.BottomLeft => new Point(0, height),
        StartMenuAnchor.Bottom => new Point(width / 2, height),
        StartMenuAnchor.BottomRight => new Point(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}

public sealed class StartMenuConfigurationException : Exception
{
    public StartMenuConfigurationException(string message) : base(message) { }
    public StartMenuConfigurationException(string message, Exception innerException) : base(message, innerException) { }
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

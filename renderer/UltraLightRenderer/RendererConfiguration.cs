using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace UltraLightRenderer;

public sealed class RendererConfiguration
{
    private RendererConfiguration(
        IReadOnlyList<WallpaperProfile> wallpapers,
        IReadOnlyList<WidgetDefinition> widgets,
        StartMenuDefinition? startMenu)
    {
        Wallpapers = wallpapers;
        Widgets = widgets;
        StartMenu = startMenu;
    }

    public IReadOnlyList<WallpaperProfile> Wallpapers { get; }
    public IReadOnlyList<WidgetDefinition> Widgets { get; }
    public StartMenuDefinition? StartMenu { get; }

    public static RendererConfiguration Load(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new RendererConfigurationException("At least one --config path is required.");

        JsonElement? wallpaper = null;
        JsonElement? widgets = null;
        JsonElement? startMenu = null;
        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            if (!File.Exists(path))
                throw new RendererConfigurationException($"Configuration file was not found: {path}");

            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new RendererConfigurationException($"Configuration root must be an object: {path}");

                var keysInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!keysInFile.Add(property.Name))
                        throw new RendererConfigurationException(
                            $"Configuration file '{path}' contains duplicate section '{property.Name}'.");

                    string section = property.Name.ToLowerInvariant();
                    if (section is not ("wallpaper" or "widgets" or "start-menu"))
                        throw new RendererConfigurationException(
                            $"Unknown top-level section '{property.Name}' in '{path}'.");
                    if (!seenSections.Add(section))
                        throw new RendererConfigurationException(
                            $"Top-level section '{property.Name}' may only appear in one configuration file.");

                    switch (section)
                    {
                        case "wallpaper": wallpaper = property.Value.Clone(); break;
                        case "widgets": widgets = property.Value.Clone(); break;
                        case "start-menu": startMenu = property.Value.Clone(); break;
                    }
                }
            }
            catch (RendererConfigurationException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new RendererConfigurationException($"Invalid JSON in '{path}': {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new RendererConfigurationException($"Could not read '{path}': {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RendererConfigurationException($"Could not read '{path}': {ex.Message}", ex);
            }
        }

        if (seenSections.Count == 0)
            throw new RendererConfigurationException("At least one of 'wallpaper', 'widgets', or 'start-menu' is required.");

        var wallpapers = new List<WallpaperProfile>();
        if (wallpaper.HasValue)
        {
            RequireKind(wallpaper.Value, JsonValueKind.Array, "wallpaper");
            int index = 0;
            foreach (JsonElement item in wallpaper.Value.EnumerateArray())
                wallpapers.Add(ParseWallpaper(item, ++index));
        }

        var widgetDefinitions = new List<WidgetDefinition>();
        if (widgets.HasValue)
        {
            RequireKind(widgets.Value, JsonValueKind.Array, "widgets");
            int index = 0;
            foreach (JsonElement item in widgets.Value.EnumerateArray())
                widgetDefinitions.Add(ParseWidget(item, ++index));
        }

        StartMenuDefinition? startMenuDefinition = startMenu.HasValue
            ? ParseStartMenu(startMenu.Value)
            : null;

        return new RendererConfiguration(wallpapers, widgetDefinitions, startMenuDefinition);
    }

    public IReadOnlyList<RendererWindowSpec> ResolveWindowSpecs()
    {
        Screen[] screens = Screen.AllScreens;
        if (screens.Length == 0)
            throw new RendererConfigurationException("Windows reported no available monitors.");

        Rectangle virtualBounds = SystemInformation.VirtualScreen;
        var specs = new List<RendererWindowSpec>();
        var logicalIds = new HashSet<string>(StringComparer.Ordinal);

        void Add(RendererWindowSpec spec)
        {
            if (!logicalIds.Add(spec.LogicalId))
                throw new RendererConfigurationException($"Logical window ID is duplicated: {spec.LogicalId}");
            specs.Add(spec);
        }

        for (int profileIndex = 0; profileIndex < Wallpapers.Count; profileIndex++)
        {
            WallpaperProfile profile = Wallpapers[profileIndex];
            if (profile.WindowIds.Count > 0)
            {
                foreach (string id in profile.WindowIds)
                {
                    Add(new RendererWindowSpec(
                        id,
                        profile.Url,
                        virtualBounds,
                        virtualBounds,
                        profile.ThreadGroup,
                        WindowKind.Wallpaper,
                        profile.Transparent));
                }
                continue;
            }

            switch (profile.Mode)
            {
                case WallpaperMode.Span:
                    Add(new RendererWindowSpec(
                        $"wallpaper-{profileIndex + 1}", profile.Url, virtualBounds, virtualBounds,
                        profile.ThreadGroup, WindowKind.Wallpaper, profile.Transparent));
                    break;
                case WallpaperMode.All:
                    for (int monitorIndex = 0; monitorIndex < screens.Length; monitorIndex++)
                    {
                        string url = profile.MonitorUrls.TryGetValue(monitorIndex + 1, out string? overrideUrl)
                            ? overrideUrl
                            : profile.Url;
                        Add(new RendererWindowSpec(
                            $"wallpaper-{profileIndex + 1}-monitor-{monitorIndex + 1}",
                            url,
                            screens[monitorIndex].Bounds,
                            virtualBounds,
                            profile.ThreadGroup,
                            WindowKind.Wallpaper,
                            profile.Transparent,
                            monitorIndex + 1));
                    }
                    break;
                case WallpaperMode.Single:
                    ValidateMonitor(profile.Monitor, screens.Length, $"Wallpaper profile {profileIndex + 1}");
                    string singleUrl = profile.MonitorUrls.TryGetValue(profile.Monitor, out string? singleOverride)
                        ? singleOverride
                        : profile.Url;
                    Add(new RendererWindowSpec(
                        $"wallpaper-{profileIndex + 1}-monitor-{profile.Monitor}",
                        singleUrl,
                        screens[profile.Monitor - 1].Bounds,
                        virtualBounds,
                        profile.ThreadGroup,
                        WindowKind.Wallpaper,
                        profile.Transparent,
                        profile.Monitor));
                    break;
                default:
                    throw new RendererConfigurationException($"Unsupported wallpaper mode: {profile.Mode}");
            }
        }

        for (int index = 0; index < Widgets.Count; index++)
        {
            WidgetDefinition widget = Widgets[index];
            ValidateMonitor(widget.MonitorNumber, screens.Length, $"Widget {index + 1}");
            Add(new RendererWindowSpec(
                $"widget-{index + 1}",
                widget.Url,
                WidgetGeometry.Resolve(widget, screens[widget.MonitorNumber - 1].Bounds),
                virtualBounds,
                widget.ThreadGroup,
                WindowKind.Widget,
                widget.Transparent,
                widget.MonitorNumber));
        }

        if (StartMenu != null)
        {
            ValidateMonitor(StartMenu.MonitorNumber, screens.Length, "Start menu");
            Add(new RendererWindowSpec(
                "start-menu",
                StartMenu.Url,
                WidgetGeometry.Resolve(StartMenu.ToWidgetDefinition(), screens[StartMenu.MonitorNumber - 1].Bounds),
                virtualBounds,
                StartMenu.ThreadGroup,
                WindowKind.StartMenu,
                StartMenu.Transparent,
                StartMenu.MonitorNumber,
                StartMenu.AutoHide,
                StartMenu.HiddenBehavior));
        }

        return specs;
    }

    private static WallpaperProfile ParseWallpaper(JsonElement element, int index)
    {
        string prefix = $"Wallpaper profile {index}";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidateProperties(element, prefix, "thread-group", "url", "mode", "monitor", "monitor-urls", "window-ids", "transparent");

        string url = NormalizeUrl(GetRequiredString(element, "url", prefix), prefix);
        string group = GetGroup(element, "wallpaper");
        string modeText = GetOptionalString(element, "mode") ?? "span";
        if (!Enum.TryParse<WallpaperMode>(modeText, true, out WallpaperMode mode))
            throw new RendererConfigurationException($"{prefix} mode must be span, all, or single.");

        int monitor = GetOptionalInt(element, "monitor") ?? 1;
        if (monitor <= 0)
            throw new RendererConfigurationException($"{prefix} monitor must be positive.");

        var monitorUrls = new Dictionary<int, string>();
        if (TryGet(element, "monitor-urls", out JsonElement monitorUrlsElement))
        {
            RequireKind(monitorUrlsElement, JsonValueKind.Object, $"{prefix} monitor-urls");
            foreach (JsonProperty property in monitorUrlsElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int monitorNumber) || monitorNumber <= 0)
                    throw new RendererConfigurationException($"{prefix} monitor-urls contains invalid monitor '{property.Name}'.");
                monitorUrls[monitorNumber] = NormalizeUrl(GetString(property.Value, $"{prefix} monitor-urls[{property.Name}]"), prefix);
            }
        }

        var windowIds = new List<string>();
        if (TryGet(element, "window-ids", out JsonElement idsElement))
        {
            RequireKind(idsElement, JsonValueKind.Array, $"{prefix} window-ids");
            foreach (JsonElement id in idsElement.EnumerateArray())
            {
                string value = GetString(id, $"{prefix} window-ids").Trim();
                if (value.Length == 0)
                    throw new RendererConfigurationException($"{prefix} window-ids cannot contain empty IDs.");
                windowIds.Add(value);
            }
        }

        return new WallpaperProfile(
            url,
            mode,
            monitor,
            monitorUrls,
            windowIds,
            GetOptionalBool(element, "transparent") ?? false,
            group);
    }

    private static WidgetDefinition ParseWidget(JsonElement element, int index)
    {
        string prefix = $"Widget {index}";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidateProperties(element, prefix,
            "x", "y", "pos-scaled", "pos-scale", "size-scaled", "size-scale", "anchor",
            "width", "w", "height", "h", "monitor", "url", "transparent", "thread-group");

        double x = GetRequiredDouble(element, "x", prefix);
        double y = GetRequiredDouble(element, "y", prefix);
        bool posScaled = GetRequiredBoolOrAlias(element, "pos-scaled", "pos-scale", prefix);
        bool sizeScaled = GetRequiredBoolOrAlias(element, "size-scaled", "size-scale", prefix);
        double width = GetRequiredDoubleOrAlias(element, "width", "w", prefix);
        double height = GetRequiredDoubleOrAlias(element, "height", "h", prefix);
        int monitor = GetRequiredInt(element, "monitor", prefix);
        string anchorText = GetRequiredString(element, "anchor", prefix);
        string url = NormalizeUrl(GetRequiredString(element, "url", prefix), prefix);

        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new RendererConfigurationException($"{prefix} x and y must be finite numbers.");
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            throw new RendererConfigurationException($"{prefix} width and height must be positive finite numbers.");
        if (monitor <= 0)
            throw new RendererConfigurationException($"{prefix} monitor must be positive.");
        if (!TryParseAnchor(anchorText, out WidgetAnchor anchor))
            throw new RendererConfigurationException($"{prefix} anchor '{anchorText}' is invalid.");
        if (posScaled && (x < -100 || x > 100 || y < -100 || y > 100))
            throw new RendererConfigurationException($"{prefix} pos-scaled x and y must be between -100 and 100.");
        if (sizeScaled && (width > 100 || height > 100))
            throw new RendererConfigurationException($"{prefix} size-scaled width and height must be between 0 and 100.");

        return new WidgetDefinition(
            x, y, posScaled, sizeScaled, anchor, width, height, monitor, url,
            GetOptionalBool(element, "transparent") ?? true, GetGroup(element, "widgets"));
    }

    private static StartMenuDefinition ParseStartMenu(JsonElement element)
    {
        const string prefix = "Start menu";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidateProperties(element, prefix,
            "x", "y", "pos-scaled", "pos-scale", "size-scaled", "size-scale", "anchor",
            "width", "w", "height", "h", "monitor", "url", "transparent", "auto-hide",
            "hidden-behavior", "thread-group");

        double x = GetRequiredDouble(element, "x", prefix);
        double y = GetRequiredDouble(element, "y", prefix);
        bool posScaled = GetRequiredBoolOrAlias(element, "pos-scaled", "pos-scale", prefix);
        bool sizeScaled = GetRequiredBoolOrAlias(element, "size-scaled", "size-scale", prefix);
        double width = GetRequiredDoubleOrAlias(element, "width", "w", prefix);
        double height = GetRequiredDoubleOrAlias(element, "height", "h", prefix);
        int monitor = GetRequiredInt(element, "monitor", prefix);
        string anchorText = GetRequiredString(element, "anchor", prefix);
        string url = NormalizeUrl(GetRequiredString(element, "url", prefix), prefix);

        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new RendererConfigurationException($"{prefix} x and y must be finite numbers.");
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            throw new RendererConfigurationException($"{prefix} width and height must be positive finite numbers.");
        if (monitor <= 0)
            throw new RendererConfigurationException($"{prefix} monitor must be positive.");
        if (!TryParseAnchor(anchorText, out WidgetAnchor anchor))
            throw new RendererConfigurationException($"{prefix} anchor '{anchorText}' is invalid.");
        if (posScaled && (x < -100 || x > 100 || y < -100 || y > 100))
            throw new RendererConfigurationException($"{prefix} pos-scaled x and y must be between -100 and 100.");
        if (sizeScaled && (width > 100 || height > 100))
            throw new RendererConfigurationException($"{prefix} size-scaled width and height must be between 0 and 100.");

        StartMenuHiddenBehavior hiddenBehavior = ParseHiddenBehavior(GetOptionalString(element, "hidden-behavior"));
        return new StartMenuDefinition(
            x, y, posScaled, sizeScaled, anchor, width, height, monitor, url,
            GetOptionalBool(element, "transparent") ?? true,
            GetOptionalBool(element, "auto-hide") ?? true,
            hiddenBehavior,
            GetGroup(element, "start-menu"));
    }

    private static StartMenuHiddenBehavior ParseHiddenBehavior(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return StartMenuHiddenBehavior.None;
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => StartMenuHiddenBehavior.None,
            "pause" or "suspend" => StartMenuHiddenBehavior.Pause,
            "unload" => StartMenuHiddenBehavior.Unload,
            _ => throw new RendererConfigurationException("Start menu hidden-behavior must be none, pause, suspend, or unload.")
        };
    }

    private static string GetGroup(JsonElement element, string defaultGroup)
    {
        string group = GetOptionalString(element, "thread-group")?.Trim() ?? defaultGroup;
        if (group.Length == 0)
            throw new RendererConfigurationException("thread-group must not be empty.");
        return group;
    }

    private static void ValidateProperties(JsonElement element, string prefix, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new RendererConfigurationException($"{prefix} contains duplicate field '{property.Name}'.");
            if (!allowedSet.Contains(property.Name))
                throw new RendererConfigurationException($"{prefix} contains unknown field '{property.Name}'.");
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        TryGet(element, name, out JsonElement value) ? GetString(value, name) : null;

    private static string GetRequiredString(JsonElement element, string name, string prefix) =>
        GetOptionalString(element, name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new RendererConfigurationException($"{prefix} requires '{name}'.");

    private static string GetString(JsonElement value, string prefix) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        _ => throw new RendererConfigurationException($"{prefix} must be a string.")
    };

    private static double GetRequiredDouble(JsonElement element, string name, string prefix) =>
        GetRequiredNumber(element, name, prefix);

    private static double GetRequiredDoubleOrAlias(JsonElement element, string name, string alias, string prefix)
    {
        if (TryGet(element, name, out JsonElement value))
            return GetNumber(value, $"{prefix} {name}");
        if (TryGet(element, alias, out value))
            return GetNumber(value, $"{prefix} {alias}");
        throw new RendererConfigurationException($"{prefix} requires '{name}'.");
    }

    private static double GetRequiredNumber(JsonElement element, string name, string prefix) =>
        TryGet(element, name, out JsonElement value)
            ? GetNumber(value, $"{prefix} {name}")
            : throw new RendererConfigurationException($"{prefix} requires '{name}'.");

    private static double GetNumber(JsonElement value, string prefix)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        throw new RendererConfigurationException($"{prefix} must be a number.");
    }

    private static int GetRequiredInt(JsonElement element, string name, string prefix)
    {
        double number = GetRequiredNumber(element, name, prefix);
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new RendererConfigurationException($"{prefix} {name} must be an integer.");
        return (int)number;
    }

    private static int? GetOptionalInt(JsonElement element, string name)
    {
        if (!TryGet(element, name, out JsonElement value))
            return null;
        double number = GetNumber(value, name);
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new RendererConfigurationException($"{name} must be an integer.");
        return (int)number;
    }

    private static bool GetRequiredBoolOrAlias(JsonElement element, string name, string alias, string prefix)
    {
        if (TryGet(element, name, out JsonElement value))
            return GetBool(value, $"{prefix} {name}");
        if (TryGet(element, alias, out value))
            return GetBool(value, $"{prefix} {alias}");
        throw new RendererConfigurationException($"{prefix} requires '{name}'.");
    }

    private static bool? GetOptionalBool(JsonElement element, string name) =>
        TryGet(element, name, out JsonElement value) ? GetBool(value, name) : null;

    private static bool GetBool(JsonElement value, string prefix)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool result))
            return result;
        throw new RendererConfigurationException($"{prefix} must be boolean.");
    }

    private static string NormalizeUrl(string value, string prefix)
    {
        try { return UrlBuilder.Normalize(value); }
        catch (Exception ex) when (ex is FormatException or UriFormatException or RendererConfigurationException)
        {
            throw new RendererConfigurationException($"{prefix} has an invalid URL: {value}", ex);
        }
    }

    private static bool TryParseAnchor(string value, out WidgetAnchor anchor)
    {
        string normalized = value.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, true, out anchor);
    }

    private static void ValidateMonitor(int monitor, int count, string prefix)
    {
        if (monitor <= 0 || monitor > count)
            throw new RendererConfigurationException($"{prefix} selects monitor {monitor}, but only {count} monitor(s) are available.");
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string prefix)
    {
        if (element.ValueKind != kind)
            throw new RendererConfigurationException($"{prefix} must be a {kind.ToString().ToLowerInvariant()}.");
    }
}

public sealed record WallpaperProfile(
    string Url,
    WallpaperMode Mode,
    int Monitor,
    IReadOnlyDictionary<int, string> MonitorUrls,
    IReadOnlyList<string> WindowIds,
    bool Transparent,
    string ThreadGroup);

public enum WallpaperMode
{
    Span,
    All,
    Single
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
    bool Transparent,
    string ThreadGroup);

public sealed record StartMenuDefinition(
    double X,
    double Y,
    bool PosScaled,
    bool SizeScaled,
    WidgetAnchor Anchor,
    double Width,
    double Height,
    int MonitorNumber,
    string Url,
    bool Transparent,
    bool AutoHide,
    StartMenuHiddenBehavior HiddenBehavior,
    string ThreadGroup)
{
    internal WidgetDefinition ToWidgetDefinition() => new(
        X, Y, PosScaled, SizeScaled, Anchor, Width, Height, MonitorNumber, Url, Transparent, ThreadGroup);
}

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

    private static int ToDimension(double value) => checked(Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero)));

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

public sealed class RendererConfigurationException : Exception
{
    public RendererConfigurationException(string message) : base(message) { }
    public RendererConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

public static class UrlBuilder
{
    public static string Normalize(string value)
    {
        string url = value.Trim();
        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            url = $"https://{url}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            throw new RendererConfigurationException($"Invalid URL: {url}");
        return uri.AbsoluteUri;
    }
}

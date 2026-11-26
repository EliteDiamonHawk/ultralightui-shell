using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace Aether;

public sealed class RendererConfiguration
{
    private RendererConfiguration(
        IReadOnlyList<GenericWindowDefinition> windows,
        IReadOnlyList<WallpaperProfile> wallpapers,
        IReadOnlyList<WidgetDefinition> widgets,
        IReadOnlyDictionary<string, JsonElement> variables,
        IReadOnlyList<string> cues)
    {
        Windows = windows;
        Wallpapers = wallpapers;
        Widgets = widgets;
        Variables = variables;
        Cues = cues;
    }

    public IReadOnlyList<GenericWindowDefinition> Windows { get; }
    public IReadOnlyList<WallpaperProfile> Wallpapers { get; }
    public IReadOnlyList<WidgetDefinition> Widgets { get; }
    public IReadOnlyDictionary<string, JsonElement> Variables { get; }
    public IReadOnlyList<string> Cues { get; }

    public static RendererConfiguration Empty { get; } = new(
        Array.Empty<GenericWindowDefinition>(),
        Array.Empty<WallpaperProfile>(),
        Array.Empty<WidgetDefinition>(),
        new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        Array.Empty<string>());

    public static RendererConfiguration Load(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new RendererConfigurationException("At least one --config path is required.");

        var documents = new List<(JsonElement Root, string Source)>();
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
                documents.Add((document.RootElement.Clone(), path));
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

        return Build(documents);
    }

    public static RendererConfiguration LoadJson(JsonElement root, string source = "inline") =>
        Build(new[] { (root.Clone(), source) });

    private static RendererConfiguration Build(IReadOnlyList<(JsonElement Root, string Source)> documents)
    {
        JsonElement? windows = null;
        JsonElement? wallpaper = null;
        JsonElement? widgets = null;
        JsonElement? variables = null;
        JsonElement? cues = null;
        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((JsonElement root, string source) in documents)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new RendererConfigurationException($"Configuration root must be an object: {source}");

            var keysInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!keysInFile.Add(property.Name))
                    throw new RendererConfigurationException(
                        $"Configuration file '{source}' contains duplicate section '{property.Name}'.");

                string section = property.Name.ToLowerInvariant();
                if (section is not ("windows" or "wallpaper" or "widgets" or "variables" or "cues"))
                    throw new RendererConfigurationException(
                        $"Unknown top-level section '{property.Name}' in '{source}'.");
                if (!seenSections.Add(section))
                    throw new RendererConfigurationException(
                        $"Top-level section '{property.Name}' may only appear in one configuration file.");

                switch (section)
                {
                    case "windows": windows = property.Value.Clone(); break;
                    case "wallpaper": wallpaper = property.Value.Clone(); break;
                    case "widgets": widgets = property.Value.Clone(); break;
                    case "variables": variables = property.Value.Clone(); break;
                    case "cues": cues = property.Value.Clone(); break;
                }
            }
        }

        if (seenSections.Count == 0)
            throw new RendererConfigurationException("At least one configuration section is required.");

        var variableValues = ParseVariables(variables);
        var cueNames = ParseCues(cues);
        var genericWindows = new List<GenericWindowDefinition>();
        if (windows.HasValue)
        {
            RequireKind(windows.Value, JsonValueKind.Array, "windows");
            int index = 0;
            foreach (JsonElement item in windows.Value.EnumerateArray())
                genericWindows.Add(ParseGenericWindow(item, ++index));
        }

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

        return new RendererConfiguration(genericWindows, wallpapers, widgetDefinitions, variableValues, cueNames);
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
            ValidateLogicalId(spec.LogicalId, "window ID");
            if (!logicalIds.Add(spec.LogicalId))
                throw new RendererConfigurationException($"Logical window ID is duplicated: {spec.LogicalId}");
            specs.Add(spec);
        }

        foreach (GenericWindowDefinition definition in Windows)
            Add(definition.Resolve(screens, virtualBounds, Variables, Cues));

        for (int profileIndex = 0; profileIndex < Wallpapers.Count; profileIndex++)
        {
            WallpaperProfile profile = Wallpapers[profileIndex];
            if (profile.WindowIds.Count > 0)
            {
                if (profile.Mode != WallpaperMode.All)
                    throw new RendererConfigurationException($"Wallpaper profile {profileIndex + 1} window-ids requires mode 'all'.");
                if (profile.WindowIds.Count != screens.Length)
                    throw new RendererConfigurationException($"Wallpaper profile {profileIndex + 1} window-ids must contain one ID per monitor.");
                for (int monitorIndex = 0; monitorIndex < screens.Length; monitorIndex++)
                {
                    string id = profile.WindowIds[monitorIndex];
                    string url = profile.MonitorUrls.TryGetValue(monitorIndex + 1, out string? overrideUrl)
                        ? overrideUrl : profile.Url;
                    Add(CreateDesktopSpec(id, url, screens[monitorIndex].Bounds, virtualBounds,
                        profile.ThreadGroup, WindowKind.Wallpaper, profile.Transparent,
                        monitorIndex + 1, profile.Bridge, Variables, Cues));
                }
                continue;
            }

            switch (profile.Mode)
            {
                case WallpaperMode.Span:
                    Add(CreateDesktopSpec(profile.Id ?? $"wallpaper-{profileIndex + 1}", profile.Url,
                        virtualBounds, virtualBounds, profile.ThreadGroup, WindowKind.Wallpaper,
                        profile.Transparent, 0, profile.Bridge, Variables, Cues));
                    break;
                case WallpaperMode.All:
                    for (int monitorIndex = 0; monitorIndex < screens.Length; monitorIndex++)
                    {
                        string url = profile.MonitorUrls.TryGetValue(monitorIndex + 1, out string? overrideUrl)
                            ? overrideUrl : profile.Url;
                        Add(CreateDesktopSpec(
                            profile.WindowIds.Count > monitorIndex
                                ? profile.WindowIds[monitorIndex]
                                : profile.Id is { } baseId
                                    ? $"{baseId}:monitor:{monitorIndex + 1}"
                                    : $"wallpaper-{profileIndex + 1}-monitor-{monitorIndex + 1}",
                            url, screens[monitorIndex].Bounds, virtualBounds, profile.ThreadGroup,
                            WindowKind.Wallpaper, profile.Transparent, monitorIndex + 1, profile.Bridge,
                            Variables, Cues));
                    }
                    break;
                case WallpaperMode.Single:
                    ValidateMonitor(profile.Monitor, screens.Length, $"Wallpaper profile {profileIndex + 1}");
                    string singleUrl = profile.MonitorUrls.TryGetValue(profile.Monitor, out string? singleOverride)
                        ? singleOverride : profile.Url;
                    Add(CreateDesktopSpec(profile.Id ?? $"wallpaper-{profileIndex + 1}-monitor-{profile.Monitor}",
                        singleUrl, screens[profile.Monitor - 1].Bounds, virtualBounds, profile.ThreadGroup,
                        WindowKind.Wallpaper, profile.Transparent, profile.Monitor, profile.Bridge,
                        Variables, Cues));
                    break;
                default:
                    throw new RendererConfigurationException($"Unsupported wallpaper mode: {profile.Mode}");
            }
        }

        for (int index = 0; index < Widgets.Count; index++)
        {
            WidgetDefinition widget = Widgets[index];
            ValidateMonitor(widget.MonitorNumber, screens.Length, $"Widget {index + 1}");
            Add(CreateDesktopSpec(widget.Id ?? $"widget-{index + 1}", widget.Url,
                WidgetGeometry.Resolve(widget, screens[widget.MonitorNumber - 1].Bounds), virtualBounds,
                widget.ThreadGroup, WindowKind.Widget, widget.Transparent, widget.MonitorNumber, widget.Bridge,
                Variables, Cues));
        }

        return specs;
    }

    private static RendererWindowSpec CreateDesktopSpec(
        string id,
        string url,
        Rectangle bounds,
        Rectangle virtualBounds,
        string group,
        WindowKind kind,
        bool transparent,
        int monitor,
        BridgePolicy? bridge,
        IReadOnlyDictionary<string, JsonElement> variables,
        IReadOnlyList<string> cues) => new(
            id, url, bounds, virtualBounds, group, kind, transparent, monitor,
            WindowInitialConfiguration.Default, bridge, variables, cues, WindowPlacement.Desktop,
            WindowZOrder.Bottom, false, false, false, false, true, null,
            new(WindowInputMode.Forwarded, WindowInputMode.Forwarded, WindowFocusMode.OnClick),
            new(), new());

    private static GenericWindowDefinition ParseGenericWindow(JsonElement element, int index)
    {
        string prefix = $"Window {index}";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidatePropertiesExact(element, prefix,
            "id", "url", "runtime-group", "geometry", "surface", "lifecycle", "input", "notifications", "bridge");

        string id = GetRequiredStringExact(element, "id", prefix).Trim();
        ValidateLogicalId(id, $"{prefix} id");
        string url = NormalizeStrictUrl(GetRequiredStringExact(element, "url", prefix), prefix);

        JsonElement geometry = GetRequiredObjectExact(element, "geometry", prefix);
        ValidatePropertiesExact(geometry, $"{prefix} geometry",
            "coordinate-space", "monitor", "anchor", "position", "size");
        JsonElement position = GetRequiredObjectExact(geometry, "position", $"{prefix} geometry");
        JsonElement size = GetRequiredObjectExact(geometry, "size", $"{prefix} geometry");
        ValidatePropertiesExact(position, $"{prefix} geometry position", "x", "y", "unit");
        ValidatePropertiesExact(size, $"{prefix} geometry size", "width", "height", "unit");

        double x = GetRequiredNumberExact(position, "x", $"{prefix} geometry position");
        double y = GetRequiredNumberExact(position, "y", $"{prefix} geometry position");
        double width = GetRequiredNumberExact(size, "width", $"{prefix} geometry size");
        double height = GetRequiredNumberExact(size, "height", $"{prefix} geometry size");
        int monitor = GetOptionalIntExact(geometry, "monitor") ?? 1;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new RendererConfigurationException($"{prefix} geometry must contain finite positive dimensions.");
        if (monitor <= 0)
            throw new RendererConfigurationException($"{prefix} monitor must be positive.");

        string coordinateSpace = (GetOptionalStringExact(geometry, "coordinate-space") ?? "monitor").Trim().ToLowerInvariant();
        if (coordinateSpace is not ("monitor" or "work-area" or "virtual-screen"))
            throw new RendererConfigurationException($"{prefix} coordinate-space must be monitor, work-area, or virtual-screen.");
        string anchorText = GetOptionalStringExact(geometry, "anchor") ?? "top-left";
        if (!TryParseAnchor(anchorText, out WidgetAnchor anchor))
            throw new RendererConfigurationException($"{prefix} anchor '{anchorText}' is invalid.");

        string positionUnit = ParseStrictUnit(GetRequiredStringExact(position, "unit", $"{prefix} geometry position"), $"{prefix} geometry position unit");
        string sizeUnit = ParseStrictUnit(GetRequiredStringExact(size, "unit", $"{prefix} geometry size"), $"{prefix} geometry size unit");

        JsonElement surface = TryGetExact(element, "surface", out JsonElement surfaceValue)
            ? surfaceValue : default;
        WindowPlacement placement = WindowPlacement.TopLevel;
        WindowZOrder zOrder = WindowZOrder.Normal;
        string? title = null;
        bool framed = false;
        bool transparent = false;
        bool resizable = false;
        bool showInTaskbar = false;
        WorkAreaReservation workArea = new();
        if (surface.ValueKind != JsonValueKind.Undefined)
        {
            RequireKind(surface, JsonValueKind.Object, $"{prefix} surface");
            ValidatePropertiesExact(surface, $"{prefix} surface",
                "kind", "title", "z-order", "transparent", "framed", "resizable", "show-in-taskbar", "work-area");
            placement = ParseSurfaceKind(GetOptionalStringExact(surface, "kind"), prefix);
            zOrder = ParseStrictZOrder(GetOptionalStringExact(surface, "z-order"), prefix);
            title = GetOptionalStringExact(surface, "title");
            transparent = GetOptionalBoolExact(surface, "transparent") ?? false;
            framed = GetOptionalBoolExact(surface, "framed") ?? false;
            resizable = GetOptionalBoolExact(surface, "resizable") ?? false;
            showInTaskbar = GetOptionalBoolExact(surface, "show-in-taskbar") ?? false;
            workArea = ParseStrictWorkArea(surface, prefix);
        }

        JsonElement lifecycle = TryGetExact(element, "lifecycle", out JsonElement lifecycleValue)
            ? lifecycleValue : default;
        WindowInitialConfiguration initial = WindowInitialConfiguration.Default;
        bool pauseOnFullscreen = true;
        if (lifecycle.ValueKind != JsonValueKind.Undefined)
        {
            RequireKind(lifecycle, JsonValueKind.Object, $"{prefix} lifecycle");
            ValidatePropertiesExact(lifecycle, $"{prefix} lifecycle",
                "initial", "pause-on-fullscreen");
            if (TryGetExact(lifecycle, "initial", out JsonElement initialValue))
                initial = ParseInitialConfiguration(initialValue, prefix);
            pauseOnFullscreen = GetOptionalBoolExact(lifecycle, "pause-on-fullscreen") ?? true;
        }

        WindowInputPolicy input = ParseStrictInputPolicy(element, prefix);
        WindowNotificationPolicy notifications = ParseStrictNotifications(element, prefix);

        if (placement == WindowPlacement.Desktop &&
            (framed || showInTaskbar || workArea.Enabled || zOrder is WindowZOrder.Topmost or WindowZOrder.TopmostWhenFocused))
            throw new RendererConfigurationException($"{prefix} desktop windows cannot be framed, taskbar-visible, work-area-reserving, or topmost.");
        if (workArea.Enabled && placement != WindowPlacement.TopLevel)
            throw new RendererConfigurationException($"{prefix} work-area reservation requires top-level placement.");
        if (input.Focus != WindowFocusMode.Never && input.Mouse == WindowInputMode.None && input.Keyboard == WindowInputMode.None)
            throw new RendererConfigurationException($"{prefix} focus policy requires an input permission.");

        return new GenericWindowDefinition(
            id, url, GetStrictGroup(element, "runtime-group", "windows", prefix), title, x, y, width, height, monitor, coordinateSpace, anchor,
            positionUnit, positionUnit, sizeUnit, sizeUnit, placement, zOrder, framed, transparent, resizable,
            showInTaskbar, initial, pauseOnFullscreen, input, notifications, workArea,
            ParseStrictBridgePolicy(element, prefix));
    }

    private static WindowPlacement ParseSurfaceKind(string? value, string prefix) =>
        (value ?? "top-level").Trim().ToLowerInvariant() switch
        {
            "desktop" => WindowPlacement.Desktop,
            "top-level" => WindowPlacement.TopLevel,
            _ => throw new RendererConfigurationException($"{prefix} surface kind must be desktop or top-level.")
        };

    private static WindowInitialConfiguration ParseInitialConfiguration(JsonElement value, string prefix)
    {
        RequireKind(value, JsonValueKind.Object, $"{prefix} lifecycle initial");
        ValidatePropertiesExact(value, $"{prefix} lifecycle initial", "visibility", "runtime");
        WindowInitialVisibility visibility = (GetOptionalStringExact(value, "visibility") ?? "shown").Trim().ToLowerInvariant() switch
        {
            "shown" => WindowInitialVisibility.Shown,
            "hidden" => WindowInitialVisibility.Hidden,
            _ => throw new RendererConfigurationException($"{prefix} lifecycle initial visibility must be shown or hidden.")
        };
        WindowInitialRuntime runtime = (GetOptionalStringExact(value, "runtime") ?? "active").Trim().ToLowerInvariant() switch
        {
            "active" => WindowInitialRuntime.Active,
            "paused" => WindowInitialRuntime.Paused,
            "unloaded" => WindowInitialRuntime.Unloaded,
            _ => throw new RendererConfigurationException($"{prefix} lifecycle initial runtime must be active, paused, or unloaded.")
        };
        if (runtime == WindowInitialRuntime.Unloaded && visibility != WindowInitialVisibility.Hidden)
            throw new RendererConfigurationException($"{prefix} lifecycle initial unloaded windows must be hidden.");
        return new(visibility, runtime);
    }

    private static WindowZOrder ParseStrictZOrder(string? value, string prefix) =>
        (value ?? "normal").Trim().ToLowerInvariant() switch
        {
            "bottom" => WindowZOrder.Bottom,
            "normal" => WindowZOrder.Normal,
            "top" => WindowZOrder.Top,
            "topmost" => WindowZOrder.Topmost,
            "topmost-when-focused" => WindowZOrder.TopmostWhenFocused,
            _ => throw new RendererConfigurationException($"{prefix} surface z-order is invalid.")
        };

    private static WindowInputPolicy ParseStrictInputPolicy(JsonElement element, string prefix)
    {
        if (!TryGetExact(element, "input", out JsonElement value))
            return new(WindowInputMode.Native, WindowInputMode.Native, WindowFocusMode.Never);
        RequireKind(value, JsonValueKind.Object, $"{prefix} input");
        ValidatePropertiesExact(value, $"{prefix} input", "mouse", "keyboard", "focus", "click-through");
        return new(
            ParseStrictInputMode(GetOptionalStringExact(value, "mouse"), $"{prefix} input mouse"),
            ParseStrictInputMode(GetOptionalStringExact(value, "keyboard"), $"{prefix} input keyboard"),
            ParseStrictFocusMode(GetOptionalStringExact(value, "focus"), $"{prefix} input focus"),
            GetOptionalBoolExact(value, "click-through") ?? false);
    }

    private static WindowNotificationPolicy ParseStrictNotifications(JsonElement element, string prefix)
    {
        if (!TryGetExact(element, "notifications", out JsonElement value))
            return new();
        RequireKind(value, JsonValueKind.Object, $"{prefix} notifications");
        ValidatePropertiesExact(value, $"{prefix} notifications", "focus", "visibility", "geometry", "lifecycle");
        return new(
            GetOptionalBoolExact(value, "focus") ?? false,
            GetOptionalBoolExact(value, "visibility") ?? false,
            GetOptionalBoolExact(value, "geometry") ?? false,
            GetOptionalBoolExact(value, "lifecycle") ?? false);
    }

    private static WorkAreaReservation ParseStrictWorkArea(JsonElement surface, string prefix)
    {
        if (!TryGetExact(surface, "work-area", out JsonElement value))
            return new();
        RequireKind(value, JsonValueKind.Object, $"{prefix} surface work-area");
        ValidatePropertiesExact(value, $"{prefix} surface work-area", "enabled", "edge", "thickness");
        bool enabled = GetOptionalBoolExact(value, "enabled") ?? false;
        string edge = (GetOptionalStringExact(value, "edge") ?? "bottom").Trim().ToLowerInvariant();
        if (edge is not ("top" or "bottom" or "left" or "right"))
            throw new RendererConfigurationException($"{prefix} surface work-area edge must be top, bottom, left, or right.");
        int thickness = GetOptionalIntExact(value, "thickness") ?? 0;
        if (enabled && thickness <= 0)
            throw new RendererConfigurationException($"{prefix} surface work-area thickness must be positive when enabled.");
        return new(enabled, edge, thickness);
    }

    private static string ParseStrictUnit(string value, string prefix) => value.Trim().ToLowerInvariant() switch
    {
        "pixels" or "percent" => value.Trim().ToLowerInvariant(),
        _ => throw new RendererConfigurationException($"{prefix} must be pixels or percent.")
    };

    private static WindowInputMode ParseStrictInputMode(string? value, string prefix) =>
        (value ?? "native").Trim().ToLowerInvariant() switch
        {
            "none" => WindowInputMode.None,
            "native" => WindowInputMode.Native,
            "forwarded" => WindowInputMode.Forwarded,
            _ => throw new RendererConfigurationException($"{prefix} must be none, native, or forwarded.")
        };

    private static WindowFocusMode ParseStrictFocusMode(string? value, string prefix) =>
        (value ?? "never").Trim().ToLowerInvariant() switch
        {
            "never" => WindowFocusMode.Never,
            "on-show" => WindowFocusMode.OnShow,
            "on-reveal" => WindowFocusMode.OnReveal,
            "on-click" => WindowFocusMode.OnClick,
            _ => throw new RendererConfigurationException($"{prefix} must be never, on-show, on-reveal, or on-click.")
        };

    private static BridgePolicy ParseStrictBridgePolicy(JsonElement element, string prefix)
    {
        if (!TryGetExact(element, "bridge", out JsonElement bridge))
            return BridgePolicy.Default;
        RequireKind(bridge, JsonValueKind.Object, $"{prefix} bridge");
        ValidatePropertiesExact(bridge, $"{prefix} bridge", "enabled", "backend-ids", "control-routes", "runtime-commands");
        bool? enabled = GetOptionalBoolExact(bridge, "enabled");
        var backendIds = ParseStrictStringArray(bridge, "backend-ids", $"{prefix} bridge");
        var controlRoutes = ParseStrictStringArray(bridge, "control-routes", $"{prefix} bridge")
            .Select(route =>
            {
                if (!route.StartsWith("/v1/", StringComparison.Ordinal))
                    throw new RendererConfigurationException($"{prefix} bridge control-routes contains an invalid route.");
                return route;
            }).ToArray();
        var runtimeCommands = ParseStrictStringArray(bridge, "runtime-commands", $"{prefix} bridge");
        foreach (string command in runtimeCommands)
            if (!RuntimeCommandNames.TryMap(command, out _))
                throw new RendererConfigurationException($"{prefix} bridge runtime-commands contains an invalid command.");
        return new(enabled, backendIds, controlRoutes, runtimeCommands);
    }

    private static string[] ParseStrictStringArray(JsonElement element, string name, string prefix)
    {
        if (!TryGetExact(element, name, out JsonElement value))
            return Array.Empty<string>();
        RequireKind(value, JsonValueKind.Array, $"{prefix} {name}");
        var values = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string text = GetStrictString(item, $"{prefix} {name}").Trim();
            if (text.Length == 0 || !values.AddUnique(text))
                throw new RendererConfigurationException($"{prefix} {name} contains an invalid or duplicate value.");
        }
        return values.ToArray();
    }

    private static WallpaperProfile ParseWallpaper(JsonElement element, int index)
    {
        string prefix = $"Wallpaper profile {index}";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidateProperties(element, prefix, "id", "thread-group", "url", "mode", "monitor", "monitor-urls", "window-ids", "transparent", "bridge");
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
                ValidateLogicalId(value, $"{prefix} window ID");
                windowIds.Add(value);
            }
        }
        return new WallpaperProfile(url, mode, monitor, monitorUrls, windowIds,
            GetOptionalBool(element, "transparent") ?? false, group,
            GetOptionalId(element, "id", prefix), ParseBridgePolicy(element, prefix));
    }

    private static WidgetDefinition ParseWidget(JsonElement element, int index)
    {
        string prefix = $"Widget {index}";
        RequireKind(element, JsonValueKind.Object, prefix);
        ValidateProperties(element, prefix,
            "id", "x", "y", "pos-scaled", "pos-scale", "size-scaled", "size-scale", "anchor",
            "width", "w", "height", "h", "monitor", "url", "transparent", "thread-group", "bridge");
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
        return new WidgetDefinition(x, y, posScaled, sizeScaled, anchor, width, height, monitor, url,
            GetOptionalBool(element, "transparent") ?? true, GetGroup(element, "widgets"),
            GetOptionalId(element, "id", prefix), ParseBridgePolicy(element, prefix));
    }

    private static WindowInputPolicy ParseInputPolicy(JsonElement element, string prefix)
    {
        if (!TryGet(element, "input", out JsonElement value))
            return new(WindowInputMode.Native, WindowInputMode.Native, WindowFocusMode.Never);
        RequireKind(value, JsonValueKind.Object, $"{prefix} input");
        ValidateProperties(value, $"{prefix} input", "mouse", "keyboard", "focus", "click-through");
        return new(ParseInputMode(GetOptionalString(value, "mouse"), $"{prefix} input mouse"),
            ParseInputMode(GetOptionalString(value, "keyboard"), $"{prefix} input keyboard"),
            ParseFocusMode(GetOptionalString(value, "focus"), $"{prefix} input focus"),
            GetOptionalBool(value, "click-through") ?? false);
    }

    private static WindowNotificationPolicy ParseNotifications(JsonElement element, string prefix)
    {
        if (!TryGet(element, "notifications", out JsonElement value))
            return new();
        RequireKind(value, JsonValueKind.Object, $"{prefix} notifications");
        ValidateProperties(value, $"{prefix} notifications", "focus", "visibility", "geometry", "lifecycle");
        return new(GetOptionalBool(value, "focus") ?? false, GetOptionalBool(value, "visibility") ?? false,
            GetOptionalBool(value, "geometry") ?? false, GetOptionalBool(value, "lifecycle") ?? false);
    }

    private static WorkAreaReservation ParseWorkArea(JsonElement element, string prefix)
    {
        if (!TryGet(element, "work-area", out JsonElement value))
            return new();
        RequireKind(value, JsonValueKind.Object, $"{prefix} work-area");
        ValidateProperties(value, $"{prefix} work-area", "enabled", "edge", "thickness");
        bool enabled = GetOptionalBool(value, "enabled") ?? false;
        string edge = (GetOptionalString(value, "edge") ?? "bottom").Trim().ToLowerInvariant();
        if (edge is not ("top" or "bottom" or "left" or "right"))
            throw new RendererConfigurationException($"{prefix} work-area edge must be top, bottom, left, or right.");
        int thickness = GetOptionalInt(value, "thickness") ?? 0;
        if (enabled && thickness <= 0)
            throw new RendererConfigurationException($"{prefix} work-area thickness must be positive when enabled.");
        return new(enabled, edge, thickness);
    }

    private static string ParseUnit(JsonElement element, string name, string prefix)
    {
        string value = (GetOptionalString(element, name) ?? "pixels").Trim().ToLowerInvariant();
        if (value is not ("pixels" or "percent"))
            throw new RendererConfigurationException($"{prefix} {name} must be pixels or percent.");
        return value;
    }

    private static WindowPlacement ParsePlacement(string? value, string prefix) =>
        (value ?? "top-level").Trim().ToLowerInvariant() switch
        {
            "desktop" => WindowPlacement.Desktop,
            "top-level" or "toplevel" or "window" => WindowPlacement.TopLevel,
            _ => throw new RendererConfigurationException($"{prefix} placement must be desktop or top-level.")
        };

    private static WindowZOrder ParseZOrder(string? value, string prefix) =>
        (value ?? "normal").Trim().ToLowerInvariant() switch
        {
            "bottom" or "below" => WindowZOrder.Bottom,
            "normal" => WindowZOrder.Normal,
            "top" or "above" => WindowZOrder.Top,
            "topmost" => WindowZOrder.Topmost,
            "topmost-when-focused" or "focus" => WindowZOrder.TopmostWhenFocused,
            _ => throw new RendererConfigurationException($"{prefix} z-order is invalid.")
        };

    private static WindowInputMode ParseInputMode(string? value, string prefix) =>
        (value ?? "native").Trim().ToLowerInvariant() switch
        {
            "none" or "disabled" => WindowInputMode.None,
            "native" => WindowInputMode.Native,
            "forwarded" or "forward" => WindowInputMode.Forwarded,
            _ => throw new RendererConfigurationException($"{prefix} must be none, native, or forwarded.")
        };

    private static WindowFocusMode ParseFocusMode(string? value, string prefix) =>
        (value ?? "never").Trim().ToLowerInvariant() switch
        {
            "never" or "none" => WindowFocusMode.Never,
            "on-show" or "show" => WindowFocusMode.OnShow,
            "on-reveal" or "reveal" => WindowFocusMode.OnReveal,
            "on-click" or "click" => WindowFocusMode.OnClick,
            _ => throw new RendererConfigurationException($"{prefix} must be never, on-show, on-reveal, or on-click.")
        };

    private static IReadOnlyDictionary<string, JsonElement> ParseVariables(JsonElement? element)
    {
        if (!element.HasValue)
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        RequireKind(element.Value, JsonValueKind.Object, "variables");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.Value.EnumerateObject())
        {
            ValidateLogicalId(property.Name, $"Variable key '{property.Name}'");
            if (!values.TryAdd(property.Name, property.Value.Clone()))
                throw new RendererConfigurationException($"Variables contains duplicate key '{property.Name}'.");
        }
        return values;
    }

    private static IReadOnlyList<string> ParseCues(JsonElement? element)
    {
        if (!element.HasValue)
            return Array.Empty<string>();
        RequireKind(element.Value, JsonValueKind.Array, "cues");
        var names = new List<string>();
        foreach (JsonElement item in element.Value.EnumerateArray())
        {
            string name = GetString(item, "cues").Trim();
            ValidateLogicalId(name, "Cue name");
            if (!names.AddUnique(name))
                throw new RendererConfigurationException($"Cues contains duplicate name '{name}'.");
        }
        return names;
    }

    private static string GetGroup(JsonElement element, string defaultGroup)
    {
        string group = GetOptionalString(element, "thread-group")?.Trim() ?? defaultGroup;
        if (group.Length == 0)
            throw new RendererConfigurationException("thread-group must not be empty.");
        return group;
    }

    private static string? GetOptionalId(JsonElement element, string name, string prefix)
    {
        if (!TryGet(element, name, out JsonElement value))
            return null;
        string id = GetString(value, $"{prefix} {name}").Trim();
        ValidateLogicalId(id, $"{prefix} {name}");
        return id;
    }

    private static BridgePolicy ParseBridgePolicy(JsonElement element, string prefix)
    {
        if (!TryGet(element, "bridge", out JsonElement bridge))
            return BridgePolicy.Default;
        RequireKind(bridge, JsonValueKind.Object, $"{prefix} bridge");
        ValidateProperties(bridge, $"{prefix} bridge", "enabled", "backend-ids", "control-routes", "runtime-commands");
        bool? enabled = GetOptionalBool(bridge, "enabled");
        var backendIds = ParseStringArray(bridge, "backend-ids", $"{prefix} bridge");
        var controlRoutes = ParseStringArray(bridge, "control-routes", $"{prefix} bridge")
            .Select(route =>
            {
                if (!route.StartsWith("/v1/", StringComparison.Ordinal))
                    throw new RendererConfigurationException($"{prefix} bridge control-routes contains an invalid route.");
                return route;
            }).ToArray();
        var runtimeCommands = ParseStringArray(bridge, "runtime-commands", $"{prefix} bridge");
        foreach (string command in runtimeCommands)
            if (!RuntimeCommandNames.TryMap(command, out _))
                throw new RendererConfigurationException($"{prefix} bridge runtime-commands contains an invalid command.");
        return new(enabled, backendIds, controlRoutes, runtimeCommands);
    }

    private static string[] ParseStringArray(JsonElement element, string name, string prefix)
    {
        if (!TryGet(element, name, out JsonElement value))
            return Array.Empty<string>();
        RequireKind(value, JsonValueKind.Array, $"{prefix} {name}");
        var values = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string text = GetString(item, $"{prefix} {name}").Trim();
            if (text.Length == 0 || !values.AddUnique(text))
                throw new RendererConfigurationException($"{prefix} {name} contains an invalid or duplicate value.");
        }
        return values.ToArray();
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

    private static void ValidatePropertiesExact(JsonElement element, string prefix, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new RendererConfigurationException($"{prefix} contains duplicate field '{property.Name}'.");
            if (!allowedSet.Contains(property.Name))
                throw new RendererConfigurationException($"{prefix} contains unknown field '{property.Name}'.");
        }
    }

    private static void ValidateLogicalId(string id, string prefix)
    {
        if (id.Length == 0 || id.Length > 128 || id.Any(character =>
                character is < 'A' or > 'Z' && character is < 'a' or > 'z' &&
                character is < '0' or > '9' && character is not ('.' or '_' or ':' or '-')))
            throw new RendererConfigurationException($"{prefix} must match [A-Za-z0-9._:-] and be at most 128 characters.");
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static bool TryGetExact(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value);

    private static string GetStrictGroup(JsonElement element, string name, string defaultValue, string prefix)
    {
        string group = GetOptionalStringExact(element, name)?.Trim() ?? defaultValue;
        if (group.Length == 0)
            throw new RendererConfigurationException($"{prefix} {name} must not be empty.");
        return group;
    }

    private static JsonElement GetRequiredObjectExact(JsonElement element, string name, string prefix)
    {
        if (!TryGetExact(element, name, out JsonElement value))
            throw new RendererConfigurationException($"{prefix} requires '{name}'.");
        RequireKind(value, JsonValueKind.Object, $"{prefix} {name}");
        return value;
    }

    private static string? GetOptionalStringExact(JsonElement element, string name) =>
        TryGetExact(element, name, out JsonElement value) ? GetStrictString(value, name) : null;

    private static string GetRequiredStringExact(JsonElement element, string name, string prefix) =>
        GetOptionalStringExact(element, name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new RendererConfigurationException($"{prefix} requires '{name}'.");

    private static string GetStrictString(JsonElement value, string prefix) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new RendererConfigurationException($"{prefix} must be a string.");

    private static double GetRequiredNumberExact(JsonElement element, string name, string prefix)
    {
        if (!TryGetExact(element, name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new RendererConfigurationException($"{prefix} requires '{name}' to be a finite number.");
        return number;
    }

    private static int? GetOptionalIntExact(JsonElement element, string name)
    {
        if (!TryGetExact(element, name, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new RendererConfigurationException($"{name} must be an integer.");
        return (int)number;
    }

    private static bool? GetOptionalBoolExact(JsonElement element, string name)
    {
        if (!TryGetExact(element, name, out JsonElement value))
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RendererConfigurationException($"{name} must be boolean.");
        return value.GetBoolean();
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        TryGet(element, name, out JsonElement value) ? GetString(value, name) : null;

    private static string GetRequiredString(JsonElement element, string name, string prefix) =>
        GetOptionalString(element, name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new RendererConfigurationException($"{prefix} requires '{name}'.");

    private static string GetString(JsonElement value, string prefix) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        _ => throw new RendererConfigurationException($"{prefix} must be a string.")
    };

    private static double GetRequiredDouble(JsonElement element, string name, string prefix) =>
        GetRequiredNumber(element, name, prefix);

    private static double? GetOptionalDouble(JsonElement element, string name) =>
        TryGet(element, name, out JsonElement value) ? GetNumber(value, name) : null;

    private static double GetRequiredDoubleOrAlias(JsonElement element, string name, string alias, string prefix) =>
        TryGet(element, name, out JsonElement value) ? GetNumber(value, $"{prefix} {name}") :
        TryGet(element, alias, out value) ? GetNumber(value, $"{prefix} {alias}") :
        throw new RendererConfigurationException($"{prefix} requires '{name}'.");

    private static double GetRequiredNumber(JsonElement element, string name, string prefix) =>
        TryGet(element, name, out JsonElement value) ? GetNumber(value, $"{prefix} {name}") :
        throw new RendererConfigurationException($"{prefix} requires '{name}'.");

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

    private static bool GetRequiredBoolOrAlias(JsonElement element, string name, string alias, string prefix) =>
        TryGet(element, name, out JsonElement value) ? GetBool(value, $"{prefix} {name}") :
        TryGet(element, alias, out value) ? GetBool(value, $"{prefix} {alias}") :
        throw new RendererConfigurationException($"{prefix} requires '{name}'.");

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
        { throw new RendererConfigurationException($"{prefix} has an invalid URL: {value}", ex); }
    }

    private static string NormalizeStrictUrl(string value, string prefix)
    {
        string url = value.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            throw new RendererConfigurationException($"{prefix} has an invalid absolute URL: {value}");
        return uri.AbsoluteUri;
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

public sealed record GenericWindowDefinition(
    string Id,
    string Url,
    string ThreadGroup,
    string? Title,
    double X,
    double Y,
    double Width,
    double Height,
    int Monitor,
    string CoordinateSpace,
    WidgetAnchor Anchor,
    string XUnit,
    string YUnit,
    string WidthUnit,
    string HeightUnit,
    WindowPlacement Placement,
    WindowZOrder ZOrder,
    bool Framed,
    bool Transparent,
    bool Resizable,
    bool ShowInTaskbar,
    WindowInitialConfiguration Initial,
    bool PauseOnFullscreen,
    WindowInputPolicy Input,
    WindowNotificationPolicy Notifications,
    WorkAreaReservation WorkArea,
    BridgePolicy Bridge)
{
    public RendererWindowSpec Resolve(Screen[] screens, Rectangle virtualBounds,
        IReadOnlyDictionary<string, JsonElement> variables, IReadOnlyList<string> cues)
    {
        if (Monitor <= 0 || Monitor > screens.Length)
            throw new RendererConfigurationException($"Window '{Id}' selects monitor {Monitor}, but only {screens.Length} monitor(s) are available.");
        Rectangle monitorBounds = screens[Monitor - 1].Bounds;
        Rectangle reference = CoordinateSpace == "virtual-screen"
            ? virtualBounds
            : CoordinateSpace == "work-area" ? screens[Monitor - 1].WorkingArea : monitorBounds;
        Rectangle bounds = GenericWindowGeometry.Resolve(this, reference);
        return new(Id, Url, bounds, virtualBounds, ThreadGroup, WindowKind.Generic, Transparent, Monitor,
            Initial, Bridge, variables, cues, Placement, ZOrder, Framed, Resizable, ShowInTaskbar,
            Input.Focus is WindowFocusMode.OnShow, PauseOnFullscreen, Title, Input, Notifications, WorkArea);
    }
}

public static class GenericWindowGeometry
{
    public static Rectangle Resolve(GenericWindowDefinition definition, Rectangle reference)
    {
        double xUnit = definition.XUnit == "percent" ? reference.Width / 100d : 1d;
        double yUnit = definition.YUnit == "percent" ? reference.Height / 100d : 1d;
        double widthUnit = definition.WidthUnit == "percent" ? reference.Width / 100d : 1d;
        double heightUnit = definition.HeightUnit == "percent" ? reference.Height / 100d : 1d;
        int width = checked(Math.Max(1, (int)Math.Round(definition.Width * widthUnit, MidpointRounding.AwayFromZero)));
        int height = checked(Math.Max(1, (int)Math.Round(definition.Height * heightUnit, MidpointRounding.AwayFromZero)));
        Point monitorAnchor = ResolveAnchor(definition.Anchor, reference);
        Point windowAnchor = GetWindowAnchor(definition.Anchor, width, height);
        int left = checked((int)Math.Round(monitorAnchor.X + definition.X * xUnit - windowAnchor.X, MidpointRounding.AwayFromZero));
        int top = checked((int)Math.Round(monitorAnchor.Y + definition.Y * yUnit - windowAnchor.Y, MidpointRounding.AwayFromZero));
        return new(left, top, width, height);
    }

    public static Point ResolveAnchor(WidgetAnchor anchor, Rectangle bounds) => anchor switch
    {
        WidgetAnchor.TopLeft => new(bounds.Left, bounds.Top), WidgetAnchor.Top => new(bounds.Left + bounds.Width / 2, bounds.Top),
        WidgetAnchor.TopRight => new(bounds.Right, bounds.Top), WidgetAnchor.Left => new(bounds.Left, bounds.Top + bounds.Height / 2),
        WidgetAnchor.Center => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), WidgetAnchor.Right => new(bounds.Right, bounds.Top + bounds.Height / 2),
        WidgetAnchor.BottomLeft => new(bounds.Left, bounds.Bottom), WidgetAnchor.Bottom => new(bounds.Left + bounds.Width / 2, bounds.Bottom),
        WidgetAnchor.BottomRight => new(bounds.Right, bounds.Bottom), _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };

    private static Point GetWindowAnchor(WidgetAnchor anchor, int width, int height) => anchor switch
    {
        WidgetAnchor.TopLeft => new(0, 0), WidgetAnchor.Top => new(width / 2, 0), WidgetAnchor.TopRight => new(width, 0),
        WidgetAnchor.Left => new(0, height / 2), WidgetAnchor.Center => new(width / 2, height / 2), WidgetAnchor.Right => new(width, height / 2),
        WidgetAnchor.BottomLeft => new(0, height), WidgetAnchor.Bottom => new(width / 2, height), WidgetAnchor.BottomRight => new(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}

public sealed record WallpaperProfile(
    string Url, WallpaperMode Mode, int Monitor, IReadOnlyDictionary<int, string> MonitorUrls,
    IReadOnlyList<string> WindowIds, bool Transparent, string ThreadGroup, string? Id = null, BridgePolicy? Bridge = null);

public enum WallpaperMode { Span, All, Single }

public sealed record WidgetDefinition(
    double X, double Y, bool PosScaled, bool SizeScaled, WidgetAnchor Anchor, double Width, double Height,
    int MonitorNumber, string Url, bool Transparent, string ThreadGroup, string? Id = null, BridgePolicy? Bridge = null);

public enum WidgetAnchor
{
    TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight
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
        Point monitorAnchor = GetMonitorAnchor(widget.Anchor, monitor);
        Point widgetAnchor = GetWidgetAnchor(widget.Anchor, width, height);
        // Preserve the legacy widget contract: scaled x/y are percentages of
        // the monitor origin, while the anchor only controls how the widget
        // itself is attached to that position. Unscaled coordinates remain
        // offsets from the selected monitor anchor.
        double placementX = (widget.PosScaled ? monitor.Left : monitorAnchor.X) + widget.X * positionUnitX;
        double placementY = (widget.PosScaled ? monitor.Top : monitorAnchor.Y) + widget.Y * positionUnitY;
        int left = checked((int)Math.Round(placementX - widgetAnchor.X, MidpointRounding.AwayFromZero));
        int top = checked((int)Math.Round(placementY - widgetAnchor.Y, MidpointRounding.AwayFromZero));
        return new(left, top, width, height);
    }

    private static int ToDimension(double value) => checked(Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero)));
    private static Point GetMonitorAnchor(WidgetAnchor anchor, Rectangle monitor) => GenericWindowGeometry.ResolveAnchor(anchor, monitor);
    private static Point GetWidgetAnchor(WidgetAnchor anchor, int width, int height) => anchor switch
    {
        WidgetAnchor.TopLeft => new(0, 0), WidgetAnchor.Top => new(width / 2, 0), WidgetAnchor.TopRight => new(width, 0),
        WidgetAnchor.Left => new(0, height / 2), WidgetAnchor.Center => new(width / 2, height / 2), WidgetAnchor.Right => new(width, height / 2),
        WidgetAnchor.BottomLeft => new(0, height), WidgetAnchor.Bottom => new(width / 2, height), WidgetAnchor.BottomRight => new(width, height),
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

internal static class ConfigurationExtensions
{
    internal static bool AddUnique<T>(this ICollection<T> values, T value) where T : notnull
    {
        if (values.Contains(value)) return false;
        values.Add(value);
        return true;
    }
}

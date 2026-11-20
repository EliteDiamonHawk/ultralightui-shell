using System.Text.Json;
using Chronos;

namespace Chronos.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            (nameof(OptionsParseAllFlags), OptionsParseAllFlags),
            (nameof(OptionsRejectsConflictingWinFlags), OptionsRejectsConflictingWinFlags),
            (nameof(OptionsParseStartupOverrides), OptionsParseStartupOverrides),
            (nameof(OptionsRejectsConflictingStartupOverrides), OptionsRejectsConflictingStartupOverrides),
            (nameof(ControlCodecRoundTrips), ControlCodecRoundTrips),
            (nameof(ControlCodecRejectsOversizedFrame), ControlCodecRejectsOversizedFrame),
            (nameof(RendererCommandsStayUnavailableWithoutRenderer), RendererCommandsStayUnavailableWithoutRenderer),
            (nameof(MissingPersistenceFilesCreateDefaults), MissingPersistenceFilesCreateDefaults),
            (nameof(PersistenceRoundTrips), PersistenceRoundTrips),
            (nameof(PersistenceMigratesLegacySettings), PersistenceMigratesLegacySettings),
            (nameof(ThemeRegistryRoundTrips), ThemeRegistryRoundTrips),
            (nameof(LogStoreDurablyDeduplicatesBatches), LogStoreDurablyDeduplicatesBatches),
            (nameof(BackendRegistryTreatsMissingAsEmpty), BackendRegistryTreatsMissingAsEmpty),
            (nameof(BackendRegistryLoadsDefinitions), BackendRegistryLoadsDefinitions),
            (nameof(WinGestureTrackerDistinguishesTapAndCombo), WinGestureTrackerDistinguishesTapAndCombo),
            (nameof(EscapePanicTrackerTriggersOnlyAfterTenRapidPresses), EscapePanicTrackerTriggersOnlyAfterTenRapidPresses),
            (nameof(EscapePanicTrackerIgnoresPressesOutsideWindow), EscapePanicTrackerIgnoresPressesOutsideWindow)
        };

        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} unit tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void OptionsParseAllFlags()
    {
        ControllerOptions options = ControllerOptions.Parse(new[]
        {
            "--config", "one.json", "--config=two.json", "--restore-state=false", "--tray=false",
            "--override-windows-key", "true", "--status-poll=false", "--renderer-path", "Aether.exe"
        });
        AssertEqual(2, options.ConfigPaths.Count);
        AssertEqual(false, options.RestoreState);
        AssertEqual(false, options.Tray);
        AssertEqual(true, options.WindowsKeyOverride);
        AssertEqual(false, options.StatusPolling);
        AssertTrue(Path.IsPathRooted(options.RendererPath));
    }

    private static void OptionsRejectsConflictingWinFlags() =>
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--win-menu-toggle" }));

    private static void OptionsParseStartupOverrides()
    {
        ControllerOptions options = ControllerOptions.Parse(new[] { "--restore-state", "true", "--tray", "true", "--status-poll", "true" });
        AssertEqual(true, options.RestoreState);
        AssertEqual(true, options.Tray);
        AssertEqual(true, options.StatusPolling);
    }

    private static void OptionsRejectsConflictingStartupOverrides()
    {
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--tray" }));
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--no-tray" }));
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--no-last-config" }));
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--restore-state", "maybe" }));
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--override-windows-key", "maybe" }));
        AssertThrows<OptionException>(() => ControllerOptions.Parse(new[] { "--no-status-poll" }));
    }

    private static void ControlCodecRoundTrips()
    {
        string id = Guid.NewGuid().ToString("D");
        string requestJson = ControlCodec.SerializeRequest(new ControlRequest(id, "GET", "/v1/pipes", null));
        AssertTrue(ControlCodec.TryParseRequest(requestJson, out ControlRequest? request, out ControlError? requestError, out _));
        AssertEqual(null, requestError);
        AssertEqual("GET", request!.Method);

        string responseJson = ControlCodec.SerializeResponse(ControlResponse.Success(id, 200, new { ok = true }));
        AssertTrue(ControlCodec.TryParseResponse(responseJson, out ControlResponse? response));
        AssertEqual(id, response!.Id);
        AssertEqual(200, response.Status);
        AssertTrue(ControlCodec.IsUnsolicitedEvent("{\"id\":null,\"event\":\"window.changed\"}"));
    }

    private static void ControlCodecRejectsOversizedFrame()
    {
        string line = new string('x', ControlCodec.MaxFrameBytes + 1);
        AssertFalse(ControlCodec.TryParseRequest(line, out _, out ControlError? error, out _));
        AssertEqual("frame_too_large", error!.Code);
    }

    private static void RendererCommandsStayUnavailableWithoutRenderer()
    {
        var service = new RendererCommandService(new AetherCommandClient());
        RendererCapabilities capabilities = service.RefreshAsync().GetAwaiter().GetResult();
        AssertFalse(service.IsTransportAvailable);
        AssertFalse(service.IsConnected);
        AssertEqual(0, capabilities.ConfigFiles.Count);
        AssertEqual("disconnected", capabilities.PipeState);
    }

    private static void PersistenceRoundTrips()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chronos-tests-{Guid.NewGuid():N}");
        try
        {
            var paths = new ChronosPaths(root, Path.Combine(root, "Chronos"), Path.Combine(root, "Chronos", "state.json"), Path.Combine(root, "Chronos", "backend-registry.json"), Path.Combine(root, "logs"));
            var persistence = new PersistenceStore(paths);
            persistence.WriteState(new[] { Path.Combine(root, "one.json") });
            AssertEqual(1, persistence.ReadState().Files.Count);
            Guid themeId = Guid.NewGuid();
            persistence.WriteState(new[] { Path.Combine(root, "one.json") }, themeId);
            AssertEqual(themeId, persistence.ReadState().ActiveThemeId);
            string ultralightRoot = Path.Combine(root, "ultralight");
            persistence.WriteSettings(new PersistentSettings(RendererPath: Path.Combine(root, "Aether.exe"), TrayEnabled: false, RestoreState: false, WindowsKeyOverride: true, PanicEscapeEnabled: false, StatusPollingEnabled: false, UltralightRoot: ultralightRoot));
            PersistentSettings settings = persistence.ReadSettings();
            AssertFalse(settings.TrayEnabled);
            AssertFalse(settings.RestoreState);
            AssertTrue(settings.WindowsKeyOverride);
            AssertFalse(settings.PanicEscapeEnabled);
            AssertFalse(settings.StatusPollingEnabled);
            AssertEqual(Path.Combine(root, "Aether.exe"), settings.RendererPath);
            AssertEqual(ultralightRoot, settings.UltralightRoot);
            AssertTrue(paths.StateFile.StartsWith(paths.ChronosRoot, StringComparison.OrdinalIgnoreCase));
            AssertFalse(File.Exists(Path.Combine(root, "state.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void MissingPersistenceFilesCreateDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chronos-missing-{Guid.NewGuid():N}");
        try
        {
            var paths = new ChronosPaths(root, Path.Combine(root, "Chronos"), Path.Combine(root, "Chronos", "state.json"), Path.Combine(root, "Chronos", "backend-registry.json"), Path.Combine(root, "logs"));
            var diagnostics = new List<string>();
            var persistence = new PersistenceStore(paths, diagnostics.Add);

            AssertTrue(File.Exists(paths.StateFile));
            AssertTrue(File.Exists(paths.ThemeRegistryFile));
            AssertTrue(File.Exists(paths.SettingsFile));
            AssertTrue(Directory.Exists(paths.ThemesDirectory));
            AssertEqual(0, persistence.ReadState().Files.Count);
            AssertEqual(0, persistence.ReadThemes().Themes.Count);
            AssertEqual(true, persistence.ReadSettings().TrayEnabled);
            AssertEqual(true, persistence.ReadSettings().PanicEscapeEnabled);
            AssertEqual<string?>(null, persistence.ReadSettings().RendererPath);
            AssertEqual<string?>(null, persistence.ReadSettings().UltralightRoot);

            BackendRegistry registry = BackendRegistry.Load(paths.BackendRegistryFile, diagnostics.Add);
            AssertTrue(File.Exists(paths.BackendRegistryFile));
            AssertEqual(0, registry.Definitions.Count);
            AssertEqual(0, diagnostics.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void PersistenceMigratesLegacySettings()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chronos-legacy-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new ChronosPaths(root, Path.Combine(root, "Chronos"), Path.Combine(root, "Chronos", "state.json"), Path.Combine(root, "Chronos", "backend-registry.json"), Path.Combine(root, "logs"));
            Directory.CreateDirectory(paths.ChronosRoot);
            File.WriteAllText(paths.SettingsFile, "{\"schemaVersion\":1,\"trayEnabled\":false,\"restoreState\":false,\"windowsKeyOverride\":true,\"statusPollingEnabled\":false}");
            var persistence = new PersistenceStore(paths);
            PersistentSettings settings = persistence.ReadSettings();
            AssertEqual(2, settings.SchemaVersion);
            AssertFalse(settings.TrayEnabled);
            AssertFalse(settings.RestoreState);
            AssertTrue(settings.WindowsKeyOverride);
            AssertTrue(settings.PanicEscapeEnabled);
            AssertFalse(settings.StatusPollingEnabled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void ThemeRegistryRoundTrips()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chronos-themes-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string first = Path.Combine(root, "one.json");
            string second = Path.Combine(root, "two.json");
            File.WriteAllText(first, "{}");
            File.WriteAllText(second, "{}");
            var paths = new ChronosPaths(root, Path.Combine(root, "Chronos"), Path.Combine(root, "Chronos", "state.json"), Path.Combine(root, "Chronos", "backend-registry.json"), Path.Combine(root, "logs"));
            var persistence = new PersistenceStore(paths);
            persistence.WriteThemes(new[] { new ThemeDefinition("Dark", new[] { first, second }) });
            ThemeRegistryState read = persistence.ReadThemes();
            AssertEqual(1, read.Themes.Count);
            AssertEqual("Dark", read.Themes[0].Name);
            AssertEqual(2, read.Themes[0].Files.Count);
            AssertTrue(read.Themes[0].Id != Guid.Empty);
            AssertTrue(paths.ThemeRegistryFile.StartsWith(paths.ChronosRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void BackendRegistryTreatsMissingAsEmpty()
    {
        BackendRegistry registry = BackendRegistry.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        AssertEqual(0, registry.Definitions.Count);
    }

    private static void LogStoreDurablyDeduplicatesBatches()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chronos-logs-{Guid.NewGuid():N}");
        string batchId = Guid.NewGuid().ToString("D");
        string sessionId = Guid.NewGuid().ToString("D");
        try
        {
            var store = new LogStore(root);
            using JsonDocument document = JsonDocument.Parse($"{{\"protocolVersion\":1,\"batchId\":\"{batchId}\",\"processId\":42,\"sessionId\":\"{sessionId}\",\"events\":[{{\"eventId\":\"{Guid.NewGuid():D}\",\"timestampUtc\":\"2026-11-17T00:00:00Z\",\"level\":\"info\",\"eventName\":\"renderer.ready\",\"properties\":{{\"path\":\"redacted\",\"windowCount\":\"1\"}}}}]}}");
            LogBatchResult first = store.Accept(document.RootElement);
            LogBatchResult second = store.Accept(document.RootElement);
            AssertFalse(first.Duplicate);
            AssertTrue(second.Duplicate);
            AssertEqual(1, Directory.GetFiles(root, "*.ndjson").Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void BackendRegistryLoadsDefinitions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chronos-registry-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"backends\":{\"nyx\":{\"executable\":\"nyx.exe\",\"arguments\":[\"--test\"],\"environment\":{\"MODE\":\"test\"}}}}\n");
            BackendRegistry registry = BackendRegistry.Load(path);
            AssertTrue(registry.Definitions.ContainsKey("nyx"));
            AssertEqual("nyx.exe", registry.Definitions["nyx"].Executable);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WinGestureTrackerDistinguishesTapAndCombo()
    {
        var tracker = new WinGestureTracker();
        AssertEqual(WinGestureDecision.Suppress, tracker.Handle(NativeMethods.VK_LWIN, true, false, false));
        AssertEqual(WinGestureDecision.Toggle, tracker.Handle(NativeMethods.VK_LWIN, false, true, false));

        AssertEqual(WinGestureDecision.Suppress, tracker.Handle(NativeMethods.VK_LWIN, true, false, false));
        AssertEqual(WinGestureDecision.ReplayAndSuppress, tracker.Handle('R', true, false, false));
        AssertEqual(WinGestureDecision.ReplayAndSuppress, tracker.Handle(NativeMethods.VK_LWIN, false, true, false));
    }

    private static void EscapePanicTrackerTriggersOnlyAfterTenRapidPresses()
    {
        var tracker = new EscapePanicGestureTracker(TimeSpan.FromSeconds(2));
        for (int index = 1; index < 10; index++)
            AssertFalse(tracker.RegisterPress(index * 100));
        AssertTrue(tracker.RegisterPress(1000));
        AssertFalse(tracker.RegisterPress(1100));
    }

    private static void EscapePanicTrackerIgnoresPressesOutsideWindow()
    {
        var tracker = new EscapePanicGestureTracker(TimeSpan.FromSeconds(2), triggerCount: 3);
        AssertFalse(tracker.RegisterPress(0));
        AssertFalse(tracker.RegisterPress(2501));
        AssertFalse(tracker.RegisterPress(2600));
        AssertTrue(tracker.RegisterPress(2700));
    }

    private static void AssertTrue(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void AssertFalse(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

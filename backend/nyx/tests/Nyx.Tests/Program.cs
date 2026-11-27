using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaosShell.Contracts;
using KhaosShell.Nyx;

namespace Nyx.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Test)[] Tests =
    {
        (nameof(OptionsParseLaunchContract), () => RunSync(OptionsParseLaunchContract)),
        (nameof(SettingsDataScopeIsolated), () => RunSync(SettingsDataScopeIsolated)),
        ("BoundedLineReader", () => RunSync(BoundedLineReaderTests.Run)),
        (nameof(SettingsDataScopeRejectsDotSegments), () => RunSync(SettingsDataScopeRejectsDotSegments)),
        (nameof(PublicSettingsRoutesRejectDotSegments), PublicSettingsRoutesRejectDotSegments),
        (nameof(LatestStateEventsAreCoalesced), LatestStateEventsAreCoalesced),
        (nameof(SubscriptionDisposeCancelsBlockedSend), SubscriptionDisposeCancelsBlockedSend),
        (nameof(ProcessRoundTrip), () => RunSync(ProcessRoundTrip)),
    };

    private static async Task<int> Main()
    {
        int failures = 0;
        foreach ((string name, Func<Task> test) in Tests)
        {
            try { await test().ConfigureAwait(false); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task RunSync(Action test)
    {
        test();
        return Task.CompletedTask;
    }

    private static void OptionsParseLaunchContract()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        BackendOptions options = BackendOptions.Parse(new[]
        {
            "--chronos-pipe", "KhaosShell-Backend-test",
            "--backend-id", "nyx", "--protocol", "1", "--sid", sid,
            "--data-root", Path.Combine(Path.GetTempPath(), "nyx-options-test")
        });
        AssertEqual("KhaosShell-Backend-test", options.PipeName);
        AssertEqual("nyx", options.BackendId);
        AssertEqual(1, options.Protocol);
    }

    private static void SettingsDataScopeIsolated()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Nyx-Settings-{Guid.NewGuid():N}");
        try
        {
            var store = new SettingsStore(root);
            store.SetDataValue("nyx.system.metrics", "json", "interval", JsonValue(7));
            AssertEqual(7, store.GetDataValue("nyx.system.metrics", "json", "interval", out bool found)!.GetValue<int>());
            AssertTrue(found);
            AssertFalse(store.GetDataValue("nyx.system.time", "json", "interval", out _ ) is not null);
        }
        finally { TryDelete(root); }
    }

    private static void SettingsDataScopeRejectsDotSegments()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Nyx-Settings-Dot-{Guid.NewGuid():N}");
        try
        {
            var store = new SettingsStore(root);
            AssertThrows<ArgumentException>(() => store.SetDataValue("..", "json", "value", JsonValue(1)));
            AssertThrows<ArgumentException>(() => store.SetDataValue("nyx.system.metrics", "..", "value", JsonValue(1)));
            AssertFalse(File.Exists(Path.Combine(root, "settings", "json.json")));
        }
        finally { TryDelete(root); }
    }

    private static async Task PublicSettingsRoutesRejectDotSegments()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        string root = Path.Combine(Path.GetTempPath(), $"Nyx-Public-Dot-{Guid.NewGuid():N}");
        BackendOptions options = BackendOptions.Parse(new[]
        {
            "--chronos-pipe", $"KhaosShell-Backend-Test-{Guid.NewGuid():N}",
            "--backend-id", "nyx", "--protocol", "1", "--sid", sid,
            "--data-root", root
        });

        try
        {
            using var runtime = new NyxRuntime(options);
            foreach (string path in new[]
            {
                "/v1/nyx/settings/data/../json/interval",
                "/v1/nyx/settings/data/%2E%2E/json/interval"
            })
            {
                BackendRequest request = new(
                    Guid.NewGuid().ToString("D"),
                    "POST",
                    "/v1/request",
                    BackendCodec.SerializeBody(new { method = "GET", path, body = (object?)null }));
                BackendResponse response = await runtime.HandleRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(400, response.Status);
                AssertEqual("argument_invalid", response.Error?.Code);
            }
        }
        finally { TryDelete(root); }
    }

    private static async Task LatestStateEventsAreCoalesced()
    {
        (BackendServer server, BackendServer.ClientSession owner, string root) = CreateTestSession();
        var producer = new TestProducer();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondDelivered = NewSignal();
        var delivered = new List<int>();
        var sendCount = 0;

        async Task SendAsync(BackendEventFrame frame, CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref sendCount);
            int value = frame.Data!.Value.GetProperty("value").GetInt32();
            if (ordinal == 1)
            {
                firstStarted.SetResult(null);
                await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (delivered) delivered.Add(value);
            if (ordinal == 2) secondDelivered.SetResult(null);
        }

        var subscription = new StreamSubscription("test-stream", "json", owner, producer, CancellationToken.None, SendAsync);
        try
        {
            await subscription.StartAsync().ConfigureAwait(false);
            producer.Publish(new { value = 1 });
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            for (var value = 2; value <= 100; value++) producer.Publish(new { value });
            releaseFirst.SetResult(null);
            await secondDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);

            AssertEqual(2, Volatile.Read(ref sendCount));
            lock (delivered) AssertEqual(100, delivered[^1]);
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            TryDelete(root);
        }
    }

    private static async Task SubscriptionDisposeCancelsBlockedSend()
    {
        (BackendServer server, BackendServer.ClientSession owner, string root) = CreateTestSession();
        var producer = new TestProducer();
        var sendStarted = NewSignal();

        async Task SendAsync(BackendEventFrame _, CancellationToken cancellationToken)
        {
            sendStarted.SetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        var subscription = new StreamSubscription("test-stream", "json", owner, producer, CancellationToken.None, SendAsync);
        try
        {
            await subscription.StartAsync().ConfigureAwait(false);
            producer.Publish(new { value = 1 });
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            AssertTrue(producer.Disposed);
        }
        finally
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            TryDelete(root);
        }
    }

    private static (BackendServer Server, BackendServer.ClientSession Owner, string Root) CreateTestSession()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        string root = Path.Combine(Path.GetTempPath(), $"Nyx-Subscription-{Guid.NewGuid():N}");
        BackendOptions options = BackendOptions.Parse(new[]
        {
            "--chronos-pipe", $"KhaosShell-Backend-Test-{Guid.NewGuid():N}",
            "--backend-id", "nyx", "--protocol", "1", "--sid", sid,
            "--data-root", root
        });
        var server = new BackendServer(options);
        return (server, new BackendServer.ClientSession(server, new MemoryStream(), CancellationToken.None), root);
    }

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestProducer : NyxStreamProducer
    {
        private Action<object?>? _publish;

        public bool Disposed { get; private set; }

        public override Task StartAsync(Action<object?> publish, CancellationToken cancellationToken)
        {
            _publish = publish;
            return Task.CompletedTask;
        }

        public void Publish(object? data) => _publish?.Invoke(data);

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static void ProcessRoundTrip()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        string pipeName = $"KhaosShell-Backend-Test-{Guid.NewGuid():N}";
        string dataRoot = Path.Combine(Path.GetTempPath(), $"Nyx-Process-{Guid.NewGuid():N}");
        string executable = LocateBackendExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        foreach (string value in new[] { "--chronos-pipe", pipeName, "--backend-id", "nyx", "--protocol", "1", "--sid", sid, "--data-root", dataRoot })
            process.StartInfo.ArgumentList.Add(value);
        process.StartInfo.Environment["NYX_DEV"] = "1";
        AssertTrue(process.Start());
        try
        {
            string? readyLine = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            AssertTrue(readyLine is not null);
            using JsonDocument ready = JsonDocument.Parse(readyLine!);
            AssertTrue(ready.RootElement.GetProperty("ready").GetBoolean());
            AssertEqual("nyx", ready.RootElement.GetProperty("backendId").GetString());

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            client.Connect(5000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);

            string healthId = Guid.NewGuid().ToString("D");
            Send(writer, healthId, "GET", "/v1/nyx/health", null);
            using (JsonDocument response = ReadResponse(reader, healthId))
            {
                AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                AssertTrue(response.RootElement.GetProperty("body").GetProperty("ok").GetBoolean());
            }

            string settingId = Guid.NewGuid().ToString("D");
            Send(writer, settingId, "PUT", "/v1/nyx/settings/data/nyx.system.metrics/json/interval", 2);
            using (JsonDocument response = ReadResponse(reader, settingId)) AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());

            string metricsId = Guid.NewGuid().ToString("D");
            Send(writer, metricsId, "GET", "/v1/nyx/system/metrics", new { fields = new[] { "memory.virtual" } });
            using (JsonDocument response = ReadResponse(reader, metricsId))
            {
                AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                AssertTrue(response.RootElement.GetProperty("body").GetProperty("memory").GetProperty("virtual").ValueKind == JsonValueKind.Object);
            }

            string streamRequestId = Guid.NewGuid().ToString("D");
            SendRaw(writer, BackendCodec.SerializeRequest(new BackendRequest(streamRequestId, "POST", "/v1/streams", BackendCodec.SerializeBody(new { dataId = "nyx.system.metrics?interval=1", type = "json" }))));
            string streamId;
            using (JsonDocument response = ReadResponse(reader, streamRequestId))
            {
                AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                streamId = response.RootElement.GetProperty("body").GetProperty("streamId").GetString()!;
            }
            using (JsonDocument eventFrame = ReadEvent(reader, streamId))
                AssertEqual("stream.data", eventFrame.RootElement.GetProperty("event").GetString());

            string closeId = Guid.NewGuid().ToString("D");
            SendRaw(writer, BackendCodec.SerializeRequest(new BackendRequest(closeId, "DELETE", $"/v1/streams/{streamId}", null)));
            using (JsonDocument response = ReadResponse(reader, closeId)) AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
            TryDelete(dataRoot);
        }
    }

    private static void Send(StreamWriter writer, string id, string method, string path, object? body)
    {
        JsonElement? element = body is null ? null : BackendCodec.SerializeBody(body);
        SendRaw(writer, BackendCodec.SerializeRequest(new BackendRequest(Guid.Parse(id).ToString("D"), "POST", "/v1/request", BackendCodec.SerializeBody(new { method, path, body = element }))));
    }

    private static void SendRaw(StreamWriter writer, string line) => writer.WriteLine(line);

    private static JsonDocument ReadResponse(StreamReader reader, string id)
    {
        while (true)
        {
            string line = reader.ReadLine() ?? throw new EndOfStreamException("Backend closed before response.");
            using JsonDocument frame = JsonDocument.Parse(line);
            if (frame.RootElement.TryGetProperty("id", out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() == id)
                return JsonDocument.Parse(line);
        }
    }

    private static JsonDocument ReadEvent(StreamReader reader, string streamId)
    {
        while (true)
        {
            string line = reader.ReadLine() ?? throw new EndOfStreamException("Backend closed before event.");
            using JsonDocument frame = JsonDocument.Parse(line);
            if (frame.RootElement.TryGetProperty("event", out JsonElement name) && name.GetString() == "stream.data" && frame.RootElement.GetProperty("streamId").GetString() == streamId)
                return JsonDocument.Parse(line);
        }
    }

    private static string LocateBackendExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !string.Equals(directory.Name, "nyx", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
        if (directory is null) throw new FileNotFoundException("Could not locate nyx directory.");
        string configuration = LocateBuildConfiguration();
        string path = Path.Combine(directory.FullName, "src", "Nyx", "bin", configuration, "net9.0-windows", "win-x64", "Nyx.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Build Nyx before running integration tests.", path);
        return path;
    }

    private static string LocateBuildConfiguration()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "Debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directory.Name, "Release", StringComparison.OrdinalIgnoreCase))
                return directory.Name;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not determine the test build configuration from the test output path.");
    }

    private static JsonNode JsonValue(int value) => System.Text.Json.Nodes.JsonValue.Create(value)!;
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
    }
    private static void AssertTrue(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void AssertFalse(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void AssertEqual<T>(T expected, T? actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
}

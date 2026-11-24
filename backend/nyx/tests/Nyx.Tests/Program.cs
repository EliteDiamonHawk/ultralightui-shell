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
    private static readonly (string Name, Action Test)[] Tests =
    {
        (nameof(OptionsParseLaunchContract), OptionsParseLaunchContract),
        (nameof(SettingsDataScopeIsolated), SettingsDataScopeIsolated),
        (nameof(ProcessRoundTrip), ProcessRoundTrip),
    };

    private static int Main()
    {
        int failures = 0;
        foreach ((string name, Action test) in Tests)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
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

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
        string path = Path.Combine(directory.FullName, "bin", "Debug", "net9.0-windows", "win-x64", "Nyx.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Build Nyx before running integration tests.", path);
        return path;
    }

    private static JsonNode JsonValue(int value) => System.Text.Json.Nodes.JsonValue.Create(value)!;
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void AssertTrue(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void AssertFalse(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void AssertEqual<T>(T expected, T? actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
}

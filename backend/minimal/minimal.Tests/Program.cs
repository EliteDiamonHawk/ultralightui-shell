using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using KhaosShell.MinimalBackend;

namespace MinimalBackend.Tests;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    {
        (nameof(OptionsParseLaunchContract), OptionsParseLaunchContract),
        (nameof(CodecRejectsInvalidRequest), CodecRejectsInvalidRequest),
        (nameof(CodecRejectsOversizedFrame), CodecRejectsOversizedFrame),
        (nameof(BoundedReaderPreservesFrames), BoundedReaderPreservesFrames),
        (nameof(ProcessRoundTrip), ProcessRoundTrip)
    };

    private static int Main()
    {
        int failures = 0;
        foreach ((string name, Action test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void OptionsParseLaunchContract()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        BackendOptions options = BackendOptions.Parse(new[]
        {
            "--chronos-pipe", @"\\.\pipe\KhaosShell-Backend-test",
            "--backend-id", "minimal",
            "--protocol", "1",
            "--sid", sid
        });

        AssertEqual("KhaosShell-Backend-test", options.PipeName);
        AssertEqual("minimal", options.BackendId);
        AssertEqual(1, options.Protocol);
        AssertEqual(sid, options.Sid);
    }

    private static void CodecRejectsInvalidRequest()
    {
        AssertFalse(BackendCodec.TryParseRequest("{\"id\":\"not-a-uuid\",\"method\":\"GET\",\"path\":\"/v1/test\"}", out _, out BackendError? error));
        AssertEqual("request_id_invalid", error!.Code);
    }

    private static void CodecRejectsOversizedFrame()
    {
        string line = new('x', BackendCodec.MaxFrameBytes + 1);
        AssertFalse(BackendCodec.TryParseRequest(line, out _, out BackendError? error));
        AssertEqual("frame_too_large", error!.Code);
    }

    private static void BoundedReaderPreservesFrames()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond\n"));
        var reader = new BoundedLineReader(stream);
        BoundedLine first = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        BoundedLine second = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        BoundedLine end = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual("first", first.Text);
        AssertEqual("second", second.Text);
        AssertTrue(end.EndOfStream);
    }

    private static void ProcessRoundTrip()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current SID unavailable.");
        string pipeName = $"KhaosShell-Backend-Test-{Guid.NewGuid():N}";
        string executable = LocateBackendExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--chronos-pipe");
        process.StartInfo.ArgumentList.Add(pipeName);
        process.StartInfo.ArgumentList.Add("--backend-id");
        process.StartInfo.ArgumentList.Add("minimal");
        process.StartInfo.ArgumentList.Add("--protocol");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--sid");
        process.StartInfo.ArgumentList.Add(sid);

        AssertTrue(process.Start());
        try
        {
            string? readinessLine = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            AssertTrue(readinessLine != null);
            using (JsonDocument readiness = JsonDocument.Parse(readinessLine!))
            {
                AssertTrue(readiness.RootElement.GetProperty("ready").GetBoolean());
                AssertEqual("minimal", readiness.RootElement.GetProperty("backendId").GetString());
                AssertEqual(pipeName, readiness.RootElement.GetProperty("pipeName").GetString());
            }

            using (NamedPipeClientStream client = Connect(pipeName))
            using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            using (var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            {
                string healthId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{healthId}\",\"method\":\"POST\",\"path\":\"/v1/request\",\"body\":{{\"method\":\"GET\",\"path\":\"/v1/minimal/health\",\"body\":null}}}}");
                using (JsonDocument response = ReadResponse(reader, healthId))
                {
                    AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                    AssertTrue(response.RootElement.GetProperty("body").GetProperty("ok").GetBoolean());
                }

                string setId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{setId}\",\"method\":\"POST\",\"path\":\"/v1/request\",\"body\":{{\"method\":\"PUT\",\"path\":\"/v1/minimal/value\",\"body\":{{\"value\":42.5}}}}}}");
                using (JsonDocument response = ReadResponse(reader, setId))
                {
                    AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                    AssertEqual(42.5, response.RootElement.GetProperty("body").GetProperty("value").GetDouble());
                }

                string streamRequestId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{streamRequestId}\",\"method\":\"POST\",\"path\":\"/v1/streams\",\"body\":{{\"dataId\":\"minimal.value\",\"type\":\"json\"}}}}");
                string streamId;
                using (JsonDocument response = ReadResponse(reader, streamRequestId))
                {
                    AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
                    streamId = response.RootElement.GetProperty("body").GetProperty("streamId").GetString()!;
                    AssertEqual("number", response.RootElement.GetProperty("body").GetProperty("schema").GetProperty("fields").GetProperty("value").GetProperty("kind").GetString());
                }

                using (JsonDocument eventFrame = ReadEvent(reader, streamId))
                {
                    AssertEqual("stream.data", eventFrame.RootElement.GetProperty("event").GetString());
                    AssertEqual(42.5, eventFrame.RootElement.GetProperty("data").GetProperty("value").GetDouble());
                }

                string incrementId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{incrementId}\",\"method\":\"POST\",\"path\":\"/v1/request\",\"body\":{{\"method\":\"POST\",\"path\":\"/v1/minimal/commands/increment\",\"body\":{{\"delta\":1.5}}}}}}");
                using (JsonDocument response = ReadResponse(reader, incrementId))
                    AssertEqual(44.0, response.RootElement.GetProperty("body").GetProperty("value").GetDouble());
                using (JsonDocument eventFrame = ReadEvent(reader, streamId))
                    AssertEqual(44.0, eventFrame.RootElement.GetProperty("data").GetProperty("value").GetDouble());

                string closeId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{closeId}\",\"method\":\"DELETE\",\"path\":\"/v1/streams/{streamId}\",\"body\":null}}");
                using (JsonDocument response = ReadResponse(reader, closeId))
                    AssertEqual(200, response.RootElement.GetProperty("status").GetInt32());
            }

            using (NamedPipeClientStream client = Connect(pipeName))
            using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            using (var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            {
                string streamRequestId = Guid.NewGuid().ToString("D");
                writer.WriteLine($"{{\"id\":\"{streamRequestId}\",\"method\":\"POST\",\"path\":\"/v1/streams\",\"body\":{{\"dataId\":\"minimal.value\",\"type\":\"json\"}}}}");
                string streamId;
                using (JsonDocument response = ReadResponse(reader, streamRequestId))
                    streamId = response.RootElement.GetProperty("body").GetProperty("streamId").GetString()!;
                using (JsonDocument eventFrame = ReadEvent(reader, streamId))
                    AssertEqual(44.0, eventFrame.RootElement.GetProperty("data").GetProperty("value").GetDouble());
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
        }
    }

    private static NamedPipeClientStream Connect(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect(5000);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

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
            if (frame.RootElement.TryGetProperty("event", out JsonElement name) &&
                name.GetString() == "stream.data" &&
                frame.RootElement.GetProperty("streamId").GetString() == streamId)
                return JsonDocument.Parse(line);
        }
    }

    private static string LocateBackendExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !string.Equals(directory.Name, "backend", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        if (directory == null)
            throw new FileNotFoundException("Could not locate backend directory.");

        string path = Path.Combine(directory.FullName, "minimal", "minimal", "bin", "Debug", "net9.0-windows", "win-x64", "MinimalBackend.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException("Build MinimalBackend before running process integration test.", path);
        return path;
    }

    private static void AssertTrue(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }

    private static void AssertFalse(bool condition)
    {
        if (condition) throw new InvalidOperationException("Expected false.");
    }

    private static void AssertEqual<T>(T expected, T? actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

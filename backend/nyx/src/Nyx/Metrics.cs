using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KhaosShell.Nyx;

internal static class MetricFields
{
    public const int MinInterval = 1;
    public const int MaxInterval = 3600;
    public const int DefaultInterval = 5;
    public const int MaxFields = 64;

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "system.boot_time",
        "cpu.utilization", "cpu.utilization.overall", "cpu.utilization.per_core",
        "cpu.cores", "cpu.cores.physical", "cpu.cores.logical",
        "cpu.frequencies", "cpu.frequencies.current_mhz", "cpu.frequencies.minimum_mhz", "cpu.frequencies.maximum_mhz", "cpu.frequencies.per_core",
        "memory.virtual", "memory.virtual.total_bytes", "memory.virtual.available_bytes", "memory.virtual.used_bytes", "memory.virtual.free_bytes", "memory.virtual.percent",
        "memory.swap", "memory.swap.total_bytes", "memory.swap.used_bytes", "memory.swap.free_bytes", "memory.swap.percent",
        "disks.partitions", "disks.partitions.mount_point", "disks.partitions.filesystem",
        "disks.usage", "disks.usage.mount_point", "disks.usage.total_bytes", "disks.usage.used_bytes", "disks.usage.free_bytes", "disks.usage.percent",
        "disks.io", "disks.io.read_bytes", "disks.io.write_bytes", "disks.io.busy_time_ms",
        "network", "network.bytes_sent", "network.bytes_received", "network.packets_dropped", "network.transmission_errors",
        "sensors.temperatures", "sensors.fans", "sensors.battery", "sensors.battery.percent", "sensors.battery.seconds_left", "sensors.battery.power_plugged", "sensors.readings",
    };

    public static bool Requested(IReadOnlySet<string>? fields, string path) =>
        fields is null || fields.Any(field => field == path || field.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(field + ".", StringComparison.Ordinal));

    public static HashSet<string>? Parse(JsonElement? value)
    {
        if (value is not JsonElement element || element.ValueKind == JsonValueKind.Null)
            return null;
        IEnumerable<string> values = element.ValueKind switch
        {
            JsonValueKind.String => (element.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            JsonValueKind.Array => element.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : throw new NyxValidationException("Metric fields must contain strings.")),
            _ => throw new NyxValidationException("Metric fields must be a comma-separated string or string array.")
        };
        var fields = values.ToHashSet(StringComparer.Ordinal);
        if (fields.Count == 0 || fields.Count > MaxFields)
            throw new NyxValidationException($"Metric fields must contain 1 through {MaxFields} fields.");
        string? invalid = fields.Except(Supported).OrderBy(valueItem => valueItem, StringComparer.Ordinal).FirstOrDefault();
        if (invalid is not null) throw new NyxValidationException($"Unsupported metric field: {invalid}");
        return fields;
    }

    public static int ValidateInterval(JsonElement? value)
    {
        if (value is not JsonElement element || element.ValueKind == JsonValueKind.Null)
            return DefaultInterval;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int interval) || interval is < MinInterval or > MaxInterval)
            throw new NyxValidationException($"Metric interval must be an integer from {MinInterval} through {MaxInterval}.");
        return interval;
    }
}

internal sealed class NyxValidationException(string message) : Exception(message);

internal interface IHardwareMonitor : IDisposable
{
    IReadOnlyList<Dictionary<string, object?>> Read();
}

internal sealed class HardwareMonitor : IHardwareMonitor
{
    private readonly bool _enabled;

    public HardwareMonitor(bool enabled) => _enabled = enabled;
    public IReadOnlyList<Dictionary<string, object?>> Read()
    {
        if (!_enabled || !OperatingSystem.IsWindows() || !GetSystemPowerStatus(out SYSTEM_POWER_STATUS status) || status.BatteryFlag == 128)
            return [];
        return [new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "system:battery:0", ["hardware_id"] = "battery", ["hardware_name"] = "Battery", ["hardware_type"] = "battery",
            ["name"] = "Battery", ["type"] = "battery", ["value"] = status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent,
            ["unit"] = "%", ["minimum"] = null, ["maximum"] = 100, ["warning"] = null, ["critical"] = null, ["source"] = "system",
        }];
    }
    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)] private struct SYSTEM_POWER_STATUS { public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent; public byte Reserved1; public uint BatteryLifeTime; public uint BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

internal sealed class SystemMetricsService : IDisposable
{
    private readonly IHardwareMonitor _hardware;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cpuGate = new();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private Dictionary<string, object?>? _cached;
    private double _cachedAt = double.MinValue;

    public SystemMetricsService(IHardwareMonitor hardware, IClock clock)
    {
        _hardware = hardware;
        _clock = clock;
    }

    public async Task<Dictionary<string, object?>> CollectAsync(IReadOnlySet<string>? fields, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            double now = _clock.MonotonicSeconds;
            if (_cached is null || now - _cachedAt >= 1)
            {
                _cached = CollectFull();
                _cachedAt = now;
            }
            return Project(_cached, fields);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _hardware.Dispose();
        _gate.Dispose();
    }

    private Dictionary<string, object?> CollectFull() => new(StringComparer.Ordinal)
    {
        ["timestamp"] = _clock.UtcNow.ToString("O"),
        ["system"] = GetSystem(),
        ["cpu"] = GetCpu(),
        ["memory"] = GetMemory(),
        ["disks"] = GetDisks(),
        ["network"] = GetNetwork(),
        ["sensors"] = GetSensors(),
    };

    private Dictionary<string, object?> GetSystem()
    {
        DateTimeOffset? boot = null;
        try { boot = _clock.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()); } catch { }
        return new(StringComparer.Ordinal) { ["available"] = boot is not null, ["boot_time"] = boot?.ToString("O") };
    }

    private Dictionary<string, object?> GetCpu()
    {
        double? overall = ReadCpuUsage();
        var perCore = new List<object?>();
        return new(StringComparer.Ordinal)
        {
            ["utilization"] = new Dictionary<string, object?> { ["available"] = overall is not null || perCore.Count > 0, ["overall"] = overall, ["per_core"] = perCore },
            ["cores"] = new Dictionary<string, object?> { ["available"] = true, ["physical"] = null, ["logical"] = Environment.ProcessorCount },
            ["frequencies"] = new Dictionary<string, object?> { ["available"] = false, ["current_mhz"] = null, ["minimum_mhz"] = null, ["maximum_mhz"] = null, ["per_core"] = new List<object?>() },
        };
    }

    private static Dictionary<string, object?> GetMemory()
    {
        long total = 0, available = 0;
        bool found = TryGetMemory(out total, out available);
        long used = Math.Max(0, total - available);
        return new(StringComparer.Ordinal)
        {
            ["available"] = found,
            ["virtual"] = new Dictionary<string, object?> { ["available"] = found, ["total_bytes"] = found ? total : null, ["available_bytes"] = found ? available : null, ["used_bytes"] = found ? used : null, ["free_bytes"] = found ? available : null, ["percent"] = found && total > 0 ? used * 100d / total : null },
            ["swap"] = new Dictionary<string, object?> { ["available"] = false, ["total_bytes"] = null, ["used_bytes"] = null, ["free_bytes"] = null, ["percent"] = null },
        };
    }

    private static Dictionary<string, object?> GetDisks()
    {
        var partitions = new List<object?>();
        var usage = new List<object?>();
        foreach (DriveInfo drive in SafeDrives())
        {
            string mount = drive.Name.TrimEnd('\\', '/');
            partitions.Add(new Dictionary<string, object?> { ["mount_point"] = mount, ["filesystem"] = SafeDriveFormat(drive) });
            try
            {
                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                usage.Add(new Dictionary<string, object?> { ["available"] = true, ["mount_point"] = mount, ["total_bytes"] = total, ["used_bytes"] = total - free, ["free_bytes"] = free, ["percent"] = total > 0 ? (total - free) * 100d / total : null });
            }
            catch { usage.Add(new Dictionary<string, object?> { ["available"] = false, ["mount_point"] = mount }); }
        }
        return new(StringComparer.Ordinal) { ["available"] = partitions.Count > 0, ["partitions"] = partitions, ["usage"] = usage, ["io"] = new Dictionary<string, object?> { ["available"] = false, ["read_bytes"] = null, ["write_bytes"] = null, ["busy_time_ms"] = null } };
    }

    private static Dictionary<string, object?> GetNetwork()
    {
        long sent = 0, received = 0, dropped = 0, errors = 0;
        bool found = false;
        try
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                var statistics = network.GetIPv4Statistics();
                sent += statistics.BytesSent; received += statistics.BytesReceived;
                dropped += statistics.IncomingPacketsDiscarded + statistics.OutgoingPacketsDiscarded;
                errors += statistics.IncomingPacketsWithErrors + statistics.OutgoingPacketsWithErrors;
                found = true;
            }
        }
        catch { }
        return new(StringComparer.Ordinal) { ["available"] = found, ["bytes_sent"] = sent, ["bytes_received"] = received, ["packets_dropped"] = dropped, ["transmission_errors"] = errors };
    }

    private Dictionary<string, object?> GetSensors()
    {
        var readings = _hardware.Read();
        var temperatures = readings.Where(item => Equals(item.GetValueOrDefault("type"), "temperature")).Select(item => (object?)new Dictionary<string, object?> { ["label"] = item.GetValueOrDefault("name"), ["current_c"] = item.GetValueOrDefault("value"), ["minimum_c"] = item.GetValueOrDefault("minimum"), ["maximum_c"] = item.GetValueOrDefault("maximum"), ["critical_c"] = item.GetValueOrDefault("critical") }).ToList();
        var fans = readings.Where(item => Equals(item.GetValueOrDefault("type"), "fan")).Select(item => (object?)new Dictionary<string, object?> { ["label"] = item.GetValueOrDefault("name"), ["current_rpm"] = item.GetValueOrDefault("value") }).ToList();
        Dictionary<string, object?> battery = new(StringComparer.Ordinal) { ["available"] = false, ["percent"] = null, ["seconds_left"] = null, ["power_plugged"] = null, ["source"] = null };
        Dictionary<string, object?>? batteryReading = readings.FirstOrDefault(item => Equals(item.GetValueOrDefault("type"), "battery"));
        if (batteryReading is not null)
            battery = new(StringComparer.Ordinal) { ["available"] = batteryReading.GetValueOrDefault("value") is not null, ["percent"] = batteryReading.GetValueOrDefault("value"), ["seconds_left"] = null, ["power_plugged"] = null, ["source"] = batteryReading.GetValueOrDefault("source") };
        return new(StringComparer.Ordinal)
        {
            ["available"] = readings.Count > 0,
            ["temperatures"] = temperatures,
            ["fans"] = fans,
            ["battery"] = battery,
            ["readings"] = readings.Cast<object?>().ToList(),
        };
    }

    private static Dictionary<string, object?> Project(Dictionary<string, object?> source, IReadOnlySet<string>? fields)
    {
        var copy = DeepCopy(source);
        if (fields is null) return copy;
        if (copy["system"] is Dictionary<string, object?> system && !MetricFields.Requested(fields, "system.boot_time")) system["boot_time"] = null;
        if (copy["cpu"] is Dictionary<string, object?> cpu)
        {
            if (cpu["utilization"] is Dictionary<string, object?> utilization)
            {
                if (!MetricFields.Requested(fields, "cpu.utilization")) utilization["available"] = false;
                if (!MetricFields.Requested(fields, "cpu.utilization.overall")) utilization["overall"] = null;
                if (!MetricFields.Requested(fields, "cpu.utilization.per_core")) utilization["per_core"] = new List<object?>();
            }
            if (cpu["cores"] is Dictionary<string, object?> cores)
            {
                if (!MetricFields.Requested(fields, "cpu.cores")) cores["available"] = false;
                if (!MetricFields.Requested(fields, "cpu.cores.physical")) cores["physical"] = null;
                if (!MetricFields.Requested(fields, "cpu.cores.logical")) cores["logical"] = null;
            }
            if (cpu["frequencies"] is Dictionary<string, object?> frequencies && !MetricFields.Requested(fields, "cpu.frequencies")) frequencies["available"] = false;
        }
        if (copy["memory"] is Dictionary<string, object?> memory)
        {
            if (memory["virtual"] is Dictionary<string, object?> virtualMemory && !MetricFields.Requested(fields, "memory.virtual")) virtualMemory["available"] = false;
            if (memory["swap"] is Dictionary<string, object?> swap && !MetricFields.Requested(fields, "memory.swap")) swap["available"] = false;
        }
        if (copy["disks"] is Dictionary<string, object?> disks)
        {
            if (!MetricFields.Requested(fields, "disks.partitions")) disks["partitions"] = new List<object?>();
            if (!MetricFields.Requested(fields, "disks.usage")) disks["usage"] = new List<object?>();
            if (disks["io"] is Dictionary<string, object?> io && !MetricFields.Requested(fields, "disks.io")) io["available"] = false;
        }
        if (copy["network"] is Dictionary<string, object?> network && !MetricFields.Requested(fields, "network")) network["available"] = false;
        if (copy["sensors"] is Dictionary<string, object?> sensors)
        {
            if (!MetricFields.Requested(fields, "sensors.temperatures") && !MetricFields.Requested(fields, "sensors.readings")) sensors["temperatures"] = new List<object?>();
            if (!MetricFields.Requested(fields, "sensors.fans") && !MetricFields.Requested(fields, "sensors.readings")) sensors["fans"] = new List<object?>();
            if (!MetricFields.Requested(fields, "sensors.readings")) sensors["readings"] = new List<object?>();
            if (!MetricFields.Requested(fields, "sensors.battery")) sensors["battery"] = new Dictionary<string, object?> { ["available"] = false, ["percent"] = null, ["seconds_left"] = null, ["power_plugged"] = null, ["source"] = null };
        }
        return copy;
    }

    private static Dictionary<string, object?> DeepCopy(Dictionary<string, object?> value) => value.ToDictionary(pair => pair.Key, pair => CopyValue(pair.Value), StringComparer.Ordinal);
    private static object? CopyValue(object? value) => value switch
    {
        Dictionary<string, object?> dictionary => DeepCopy(dictionary),
        List<object?> list => list.Select(CopyValue).ToList(),
        _ => value,
    };

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try { return DriveInfo.GetDrives().Where(drive => drive.IsReady).ToArray(); } catch { return []; }
    }

    private static string? SafeDriveFormat(DriveInfo drive) { try { return drive.DriveFormat; } catch { return null; } }
    private static bool TryGetMemory(out long total, out long available)
    {
        total = available = 0;
        try
        {
            var value = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref value)) return false;
            total = checked((long)value.ullTotalPhys); available = checked((long)value.ullAvailPhys); return total > 0;
        }
        catch { return false; }
    }

    private double? ReadCpuUsage()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user)) return null;
        ulong idleValue = ToUInt64(idle);
        ulong kernelValue = ToUInt64(kernel);
        ulong userValue = ToUInt64(user);
        lock (_cpuGate)
        {
            if (_previousKernel == 0)
            {
                _previousIdle = idleValue;
                _previousKernel = kernelValue;
                _previousUser = userValue;
                return null;
            }
            ulong idleDelta = idleValue - _previousIdle;
            ulong totalDelta = (kernelValue - _previousKernel) + (userValue - _previousUser);
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            return totalDelta == 0 ? null : Math.Clamp((totalDelta - Math.Min(idleDelta, totalDelta)) * 100d / totalDelta, 0, 100);
        }
    }

    private static ulong ToUInt64(FILETIME value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX value);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
    double MonotonicSeconds { get; }
}

internal sealed class SystemClock : IClock
{
    private readonly long _started = Stopwatch.GetTimestamp();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public double MonotonicSeconds => (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency;
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaosShell.Nyx;

internal sealed class SettingsStore
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _appPath;
    private readonly string _dataRoot;

    public SettingsStore(string dataRoot)
    {
        _root = Path.Combine(dataRoot, "settings");
        _appPath = Path.Combine(_root, "app.json");
        _dataRoot = Path.Combine(_root, "data");
        Initialize();
    }

    public JsonObject GetApp()
    {
        lock (_gate) return LoadApp()["app"]!.AsObject().DeepClone().AsObject();
    }

    public JsonNode? GetAppValue(string key, out bool found)
    {
        lock (_gate)
        {
            JsonObject values = LoadApp()["app"]!.AsObject();
            found = values.ContainsKey(key);
            return found ? values[key]?.DeepClone() : null;
        }
    }

    public void SetAppValue(string key, JsonNode? value)
    {
        lock (_gate)
        {
            JsonObject document = LoadApp();
            document["app"]!.AsObject()[key] = value?.DeepClone();
            Write(_appPath, document);
        }
    }

    public bool DeleteAppValue(string key)
    {
        lock (_gate)
        {
            JsonObject document = LoadApp();
            if (!document["app"]!.AsObject().Remove(key)) return false;
            Write(_appPath, document);
            return true;
        }
    }

    public JsonObject GetData(string dataId, string datatype)
    {
        ValidateScope(dataId, datatype);
        lock (_gate)
        {
            string path = ScopePath(dataId, datatype);
            if (!File.Exists(path)) return [];
            JsonObject document = Load(path);
            ValidateDocument(document, dataId, datatype);
            return document["settings"]!.AsObject().DeepClone().AsObject();
        }
    }

    public JsonNode? GetDataValue(string dataId, string datatype, string key, out bool found)
    {
        JsonObject values = GetData(dataId, datatype);
        found = values.ContainsKey(key);
        return found ? values[key]?.DeepClone() : null;
    }

    public void SetDataValue(string dataId, string datatype, string key, JsonNode? value)
    {
        ValidateScope(dataId, datatype);
        lock (_gate)
        {
            JsonObject document = File.Exists(ScopePath(dataId, datatype))
                ? Load(ScopePath(dataId, datatype))
                : new JsonObject { ["version"] = Version, ["dataId"] = dataId, ["datatype"] = datatype, ["settings"] = new JsonObject() };
            ValidateDocument(document, dataId, datatype);
            document["settings"]!.AsObject()[key] = value?.DeepClone();
            Write(ScopePath(dataId, datatype), document);
        }
    }

    public bool DeleteDataValue(string dataId, string datatype, string key)
    {
        ValidateScope(dataId, datatype);
        lock (_gate)
        {
            string path = ScopePath(dataId, datatype);
            if (!File.Exists(path)) return false;
            JsonObject document = Load(path);
            ValidateDocument(document, dataId, datatype);
            if (!document["settings"]!.AsObject().Remove(key)) return false;
            Write(path, document);
            return true;
        }
    }

    private void Initialize()
    {
        Directory.CreateDirectory(_dataRoot);
        if (!File.Exists(_appPath))
            Write(_appPath, new JsonObject { ["version"] = Version, ["app"] = new JsonObject() });
    }

    private JsonObject LoadApp()
    {
        JsonObject document = Load(_appPath);
        if (document["version"]?.GetValue<int>() != Version || document["app"] is not JsonObject)
            throw new InvalidDataException("App settings document has an unsupported format or version.");
        return document;
    }

    private static JsonObject Load(string path) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"Settings document is invalid: {path}");

    private static void ValidateDocument(JsonObject document, string dataId, string datatype)
    {
        if (document["version"]?.GetValue<int>() != Version ||
            document["dataId"]?.GetValue<string>() != dataId ||
            document["datatype"]?.GetValue<string>() != datatype ||
            document["settings"] is not JsonObject)
            throw new InvalidDataException("Data settings document has an unsupported format or version.");
    }

    private string ScopePath(string dataId, string datatype) => Path.Combine(_dataRoot, Encode(dataId), Encode(datatype) + ".json");

    private static void ValidateScope(string dataId, string datatype)
    {
        if (string.IsNullOrWhiteSpace(dataId) || string.IsNullOrWhiteSpace(datatype) ||
            dataId.Contains('/') || dataId.Contains('\\') || dataId.Contains('\0') || dataId.Contains('?') || dataId.Contains('#') ||
            datatype.Contains('/') || datatype.Contains('\\') || datatype.Contains('\0'))
            throw new ArgumentException("Data ID or datatype is invalid.");
    }

    private static string Encode(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        const string hex = "0123456789ABCDEF";
        var builder = new System.Text.StringBuilder(bytes.Length);
        foreach (byte valueByte in bytes)
        {
            bool safe = valueByte is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
            if (safe) builder.Append((char)valueByte);
            else builder.Append('%').Append(hex[valueByte >> 4]).Append(hex[valueByte & 15]);
        }
        return builder.ToString();
    }

    private static void Write(string path, JsonObject document) => AtomicFile.WriteJson(path, document.ToJsonString(WriteOptions) + "\n");
}

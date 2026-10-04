

using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace OppoUnlockToolbox.Core;

public static class StateStore
{
    private static readonly object Lock = new();

    private static JsonObject Blank()
    {
        var data = new JsonObject
        {
            ["version"] = 1,
            ["device"] = new JsonObject(),
            ["partitions"] = new JsonObject(),
            ["stages"] = new JsonObject(),
            ["last_run"] = null,
        };
        foreach (var part in AppConfig.Partitions)
        {
            data["partitions"]![part] = new JsonObject
            {
                ["backup"] = null,
                ["backups"] = new JsonArray(),
                ["written"] = null,
                ["restored"] = null,
            };
        }
        return data;
    }

    public static JsonObject Load()
    {
        lock (Lock)
        {
            var path = AppConfig.StateFile();
            JsonObject? data = null;
            if (File.Exists(path))
            {
                try
                {
                    data = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                }
                catch
                {
                    data = null;
                }
            }
            if (data is null)
                data = Blank();

            if (!data.ContainsKey("version")) data["version"] = 1;
            if (data["device"] is not JsonObject) data["device"] = new JsonObject();
            if (data["partitions"] is not JsonObject) data["partitions"] = new JsonObject();
            if (data["stages"] is not JsonObject) data["stages"] = new JsonObject();
            if (!data.ContainsKey("last_run")) data["last_run"] = null;
            foreach (var part in AppConfig.Partitions)
            {
                if (data["partitions"]![part] is not JsonObject)
                    data["partitions"]![part] = new JsonObject
                    {
                        ["backup"] = null,
                        ["backups"] = new JsonArray(),
                        ["written"] = null,
                        ["restored"] = null,
                    };
                else if (data["partitions"]![part]!["backups"] is not JsonArray)
                    data["partitions"]![part]!["backups"] = new JsonArray();
            }
            return data;
        }
    }

    public static void Save(JsonObject data)
    {
        lock (Lock)
        {
            data["last_run"] = DateTimeOffset.Now.ToUnixTimeSeconds();
            var path = AppConfig.StateFile();
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {

            }
        }
    }

    public static JsonObject Update(Action<JsonObject> mutator)
    {
        lock (Lock)
        {
            var data = Load();
            mutator(data);
            Save(data);
            return data;
        }
    }

    public static JsonObject SetDevice(Dictionary<string, object?> info)
    {
        return Update(data =>
        {
            var device = data["device"]!.AsObject();
            foreach (var (key, value) in info)
            {
                if (value is null)
                    continue;
                device[key] = value switch
                {
                    bool b => System.Text.Json.Nodes.JsonValue.Create(b),
                    int i => System.Text.Json.Nodes.JsonValue.Create(i),
                    long l => System.Text.Json.Nodes.JsonValue.Create(l),
                    string s => System.Text.Json.Nodes.JsonValue.Create(s),
                    _ => System.Text.Json.Nodes.JsonValue.Create(value.ToString()),
                };
            }
            device["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds();
        });
    }

    public const string KindStock = "stock";
    public const string KindUnknown = "unknown";

    public static JsonObject RecordBackup(string part, string local, long size, string md5,
        string slot = "", string serial = "", string kind = KindStock)
    {
        return Update(data =>
        {
            var entry = EnsurePartition(data, part);
            if (entry["backups"] is not JsonArray history)
            {
                history = new JsonArray();
                entry["backups"] = history;
            }
            var record = new JsonObject
            {
                ["local"] = local,
                ["size"] = size,
                ["md5"] = md5,
                ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["slot"] = slot,
                ["serial"] = serial,
                ["kind"] = kind,
            };
            entry["backup"] = record.DeepClone();
            history.Insert(0, record.DeepClone());
            while (history.Count > 20)
                history.RemoveAt(history.Count - 1);
            entry["restored"] = null;
        });
    }

    public static string RecordString(JsonObject? record, string key)
    {
        if (record is null)
            return "";
        try
        {
            return record[key]?.GetValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static JsonObject? BackupRecord(string part) => Load()["partitions"]?[part]?["backup"] as JsonObject;

    public static string BackupMd5(string part) => RecordString(BackupRecord(part), "md5");

    public static string BackupKind(string part)
    {
        var kind = RecordString(BackupRecord(part), "kind");
        return kind.Length > 0 ? kind : KindStock;
    }

    private static IEnumerable<JsonObject> BackupRecords(string part)
    {
        var entry = Load()["partitions"]?[part] as JsonObject;
        if (entry?["backups"] is JsonArray history)
        {
            foreach (var node in history)
                if (node is JsonObject record)
                    yield return record;
        }
        if (entry?["backup"] is JsonObject current)
            yield return current;
    }

    public static List<string> StockMd5s(string part)
    {
        var result = new List<string>();
        foreach (var record in BackupRecords(part))
        {
            var kind = RecordString(record, "kind");
            var md5 = RecordString(record, "md5");
            if (md5.Length == 0 || result.Contains(md5))
                continue;
            if (kind.Length > 0 && kind != KindStock)
                continue;
            result.Add(md5);
        }
        return result;
    }

    public static JsonObject? NewestStockBackup(string part)
    {
        foreach (var record in BackupRecords(part))
        {
            var kind = RecordString(record, "kind");
            if (kind.Length > 0 && kind != KindStock)
                continue;
            var local = RecordString(record, "local");
            if (local.Length > 0 && File.Exists(local))
                return record;
        }
        return null;
    }

    public static string WrittenMd5(string part) =>
        RecordString(Load()["partitions"]?[part]?["written"] as JsonObject, "md5");

    public static long WrittenSize(string part)
    {
        var written = Load()["partitions"]?[part]?["written"] as JsonObject;
        try
        {
            return written?["size"]?.GetValue<long>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static JsonObject RecordWrite(string part, string source, long size, string md5)
    {
        return Update(data =>
        {
            var entry = EnsurePartition(data, part);
            entry["written"] = new JsonObject
            {
                ["source"] = source,
                ["size"] = size,
                ["md5"] = md5,
                ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            };
            entry["restored"] = null;
        });
    }

    public static JsonObject RecordRestore(string part, string source)
    {
        return Update(data =>
        {
            var entry = EnsurePartition(data, part);
            entry["restored"] = new JsonObject
            {
                ["source"] = source,
                ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            };
            entry["written"] = null;
        });
    }

    public static JsonObject MarkStage(string sid, bool done, string note = "")
    {
        return Update(data =>
        {
            data["stages"]![sid] = new JsonObject
            {
                ["done"] = done,
                ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["note"] = note,
            };
        });
    }

    public static bool StageDone(string sid)
    {
        return Load()["stages"]![sid] is JsonObject stage && stage["done"]!.GetValue<bool>();
    }

    public static bool HasBackup(string part)
    {
        var backup = Load()["partitions"]![part]?["backup"] as JsonObject;
        if (backup is null)
            return false;
        var local = backup["local"]?.GetValue<string>() ?? "";
        return local.Length > 0 && File.Exists(local);
    }

    public static string BackupPath(string part)
    {
        return (Load()["partitions"]![part]?["backup"] as JsonObject)?["local"]?.GetValue<string>() ?? "";
    }

    public static JsonObject Reset()
    {
        var data = Blank();
        Save(data);
        return data;
    }

    private static JsonObject EnsurePartition(JsonObject data, string part)
    {
        if (data["partitions"]![part] is not JsonObject entry)
        {
            entry = new JsonObject { ["backup"] = null, ["written"] = null, ["restored"] = null };
            data["partitions"]![part] = entry;
        }
        return entry;
    }
}

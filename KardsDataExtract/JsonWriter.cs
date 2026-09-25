using System.Text;
using System.Text.Json;

namespace KardsDataExtract;

/// <summary>把 CardRecord 列表写成 JSON。用 System.Text.Json 保证转义正确。</summary>
public static class JsonWriter
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string path, List<CardRecord> records)
    {
        var arr = new List<Dictionary<string, object>>(records.Count);
        foreach (var r in records)
            arr.Add(new Dictionary<string, object>
            {
                ["asset"] = r.asset,
                ["id"] = r.id,
                ["name"] = r.name,
                ["text"] = r.text,
                ["type"] = r.type,
                ["faction"] = r.faction,
                ["rarity"] = r.rarity,
                ["cardSet"] = r.cardSet,
                ["spawnCardName"] = r.spawnCardName,
                ["gameplayTags"] = r.gameplayTags,
                ["chooseOneCards"] = r.chooseOneCards,
                ["usedTriggers"] = r.usedTriggers,
                ["flags"] = r.flags,
                ["raw"] = r.raw,
            });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        JsonSerializer.Serialize(w, arr, Opt);
    }
}
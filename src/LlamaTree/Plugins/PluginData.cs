using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Platform;

namespace LlamaTree.Plugins;

/// <summary>
/// What each plugin keeps for itself: one JSON value per plugin, in plugin-data/&lt;id&gt;.json in the app's data folder.
/// A plugin's page has no storage of its own (its frame's origin is opaque), so it saves through the app with
/// setData and reads it back with getData. Only that plugin can read or change its own, so it needs no access you allow;
/// it's removed with the plugin. Thread safe.
/// </summary>
internal sealed class PluginData(string folder)
{
    /// <summary>The most a plugin may keep, as UTF-8 JSON.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);

    private string PathFor(string id) =>
        PluginCatalog.IsValidId(id) ? Path.Combine(folder, id + ".json") : throw new ArgumentException($"\"{id}\" isn't a plugin's id.");

    /// <summary>What the plugin last saved, or null if it never has.</summary>
    public JsonNode? Get(string id)
    {
        var path = PathFor(id);
        lock (gates.GetOrAdd(id, _ => new object()))
            return JsonFile.Read<JsonNode>(path);
    }

    /// <summary>Replaces what the plugin keeps with <paramref name="data"/>; null removes it.</summary>
    /// <exception cref="InvalidOperationException">It's more than <see cref="MaxBytes"/>.</exception>
    public void Set(string id, JsonElement data)
    {
        var path = PathFor(id);
        var json = data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : data.GetRawText();
        if (json != null && Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidOperationException($"A plugin can keep up to {MaxBytes / 1024 / 1024} MB; this is more.");
        lock (gates.GetOrAdd(id, _ => new object()))
        {
            if (json == null)
            {
                Delete(path);
                return;
            }
            PrivateFiles.CreateDirectory(folder);
            JsonFile.Write(path, json);
        }
    }

    /// <summary>Removes what a plugin kept (when the plugin is removed).</summary>
    public void Forget(string id)
    {
        if (!PluginCatalog.IsValidId(id)) return;
        lock (gates.GetOrAdd(id, _ => new object()))
            Delete(PathFor(id));
    }

    private static void Delete(string path)
    {
        foreach (var file in new[] { path, JsonFile.BackupPath(path) })
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

using System.Text.Json;
using LlamaTree.Plugins;

namespace LlamaTree.Tests;

/// <summary>What plugins keep for themselves through the app (getData and setData), and running in the background.</summary>
public sealed class PluginDataTests : IDisposable
{
    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_plugin_gets_back_what_it_saved_and_nothing_before()
    {
        var data = new PluginData(folder.File("plugin-data"));
        Assert.Null(data.Get("notes"));
        data.Set("notes", Json("""{ "sessions": { "s1": ["Decided on PaymentIntents"] }, "v": 2 }"""));
        var back = data.Get("notes")!;
        Assert.Equal("Decided on PaymentIntents", back["sessions"]!["s1"]![0]!.GetValue<string>());
        Assert.Equal(2, back["v"]!.GetValue<int>());
        Assert.Null(new PluginData(folder.File("plugin-data")).Get("other"));   // each plugin has its own
    }

    [Fact]
    public void Saving_null_or_removing_the_plugin_deletes_it()
    {
        var data = new PluginData(folder.File("plugin-data"));
        data.Set("notes", Json("[1, 2]"));
        data.Set("notes", Json("null"));
        Assert.Null(data.Get("notes"));
        data.Set("notes", Json("""{ "a": 1 }"""));
        data.Set("notes", Json("""{ "a": 2 }"""));   // leaves a backup of the first
        data.Forget("notes");
        Assert.Empty(Directory.EnumerateFiles(folder.File("plugin-data")));
    }

    [Fact]
    public void Too_much_is_refused_and_an_id_must_be_a_plugins()
    {
        var data = new PluginData(folder.File("plugin-data"));
        var big = JsonSerializer.Serialize(new string('x', PluginData.MaxBytes));
        Assert.Throws<InvalidOperationException>(() => data.Set("notes", Json(big)));
        Assert.Null(data.Get("notes"));
        Assert.Throws<ArgumentException>(() => data.Set("../escape", Json("1")));
        Assert.Throws<ArgumentException>(() => data.Get("a\\b"));
    }

    [Fact]
    public void A_plugin_can_ask_to_run_in_the_background()
    {
        foreach (var (name, manifest) in new[] { ("quiet", """{ "background": true }"""), ("plain", "{}"), ("odd", """{ "background": "yes" }""") })
        {
            var dir = Path.Combine(folder.Path, "plugins", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, PluginCatalog.Manifest), manifest);
            File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html>");
        }
        var (plugins, problems) = PluginCatalog.Load(Path.Combine(folder.Path, "plugins"));
        Assert.Empty(problems);
        Assert.Equal(["quiet"], plugins.Where(p => p.Background).Select(p => p.Id));
    }
}

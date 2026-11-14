namespace LlamaTree.Tests;

/// <summary>The app reads the clipboard with the TryGetData methods, not the obsolete GetData(string) (build warning WFDEV005).</summary>
public sealed class ClipboardSourceTests
{
    [Fact]
    public void The_app_reads_the_clipboard_without_the_obsolete_GetData()
    {
        // The source tree is found from the test build's folder, up to the solution file.
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "LlamaTree.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var source = File.ReadAllText(Path.Combine(root!, "src", "LlamaTree", "Hosting", "WinForms", "MainForm.cs"));
        Assert.DoesNotContain("Clipboard.GetData(", source);
    }
}

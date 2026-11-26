namespace LlamaTree.Platform;

/// <summary>Where the app's own files are: its page (wwwroot) and icon.</summary>
internal static class AppFiles
{
    /// <summary>
    /// Beside the program, or in a macOS app bundle, its Resources folder (Contents/Resources, beside Contents/MacOS):
    /// macOS expects only code in Contents/MacOS, where each file is signed on its own (see tools/package-macos.sh).
    /// </summary>
    public static string Folder { get; } = Find(AppContext.BaseDirectory, OperatingSystem.IsMacOS());

    internal static string Find(string programFolder, bool macOS)
    {
        if (macOS)
        {
            var resources = Path.GetFullPath(Path.Combine(programFolder, "..", "Resources"));
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(programFolder)) == "MacOS" && Directory.Exists(Path.Combine(resources, "wwwroot")))
                return resources;
        }
        return programFolder;
    }
}

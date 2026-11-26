using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>Where the app finds its page and icon: beside the program, or in a macOS app bundle's Resources folder.</summary>
public sealed class AppFilesTests
{
    [Fact]
    public void A_Mac_app_bundle_has_them_in_Resources()
    {
        using var temp = new TempFolder();
        var macOS = Directory.CreateDirectory(Path.Combine(temp.Path, "LlamaTree.app", "Contents", "MacOS")).FullName + Path.DirectorySeparatorChar;
        var resources = Directory.CreateDirectory(Path.Combine(temp.Path, "LlamaTree.app", "Contents", "Resources", "wwwroot")).Parent!.FullName;

        Assert.Equal(resources, AppFiles.Find(macOS, macOS: true));
        // Only on a Mac.
        Assert.Equal(macOS, AppFiles.Find(macOS, macOS: false));
    }

    [Fact]
    public void Otherwise_they_are_beside_the_program()
    {
        using var temp = new TempFolder();
        // Run from a build folder on a Mac, or a folder named MacOS with no Resources/wwwroot beside it.
        var build = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(temp.Path, "Resources", "wwwroot"));
        Assert.Equal(build, AppFiles.Find(build, macOS: true));

        var macOS = Directory.CreateDirectory(Path.Combine(temp.Path, "Other", "MacOS")).FullName + Path.DirectorySeparatorChar;
        Assert.Equal(macOS, AppFiles.Find(macOS, macOS: true));
    }
}

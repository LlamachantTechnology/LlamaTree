using System.Text.Json;
using LlamaTree.Sessions;
using LlamaTree.Updates;

namespace LlamaTree.Tests;

/// <summary>Reading GitHub's latest release, and carrying sessions' conversations across the restart an update causes.</summary>
public sealed class UpdateServiceTests
{
    private const string Hash = "6cbe260bf8bb8fb717de70f8c4f4030045dd05fd7340c84ab78561d108017f48";
    private const string Other = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Release(string tag = "v1.0.8", string? digest = "sha256:" + Hash, string notes = "### New\n- Things",
        string asset = "LlamaTree-Setup-1.0.8.exe", bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag, name = "LlamaTree 1.0.8", html_url = "https://github.com/LlamachantTechnology/LlamaTree/releases/tag/" + tag,
        body = notes, draft = false, prerelease,
        assets = new object[]
        {
            new { name = "notes.txt", size = 10, digest = (string?)null, browser_download_url = "https://github.com/x/notes.txt" },
            new { name = asset, size = 41932242, digest, browser_download_url = "https://github.com/LlamachantTechnology/LlamaTree/releases/download/" + tag + "/" + asset },
        },
    });

    [Fact]
    public void The_installer_and_GitHubs_checksum_are_read_from_the_release()
    {
        var u = UpdateService.Parse(Release());

        Assert.NotNull(u);
        Assert.Equal(new Version(1, 0, 8), u.Version);
        Assert.Equal("LlamaTree-Setup-1.0.8.exe", u.AssetName);
        Assert.EndsWith("/releases/download/v1.0.8/LlamaTree-Setup-1.0.8.exe", u.AssetUrl);
        Assert.Equal(41932242, u.Size);
        Assert.Equal(Hash, u.Sha256);
    }

    [Fact]
    public void An_installer_under_the_name_from_before_the_rename_is_not_used()
    {
        Assert.Null(UpdateService.Parse(Release(asset: "ClaudeTerminals-Setup-1.0.8.exe")));
    }

    [Fact]
    public void Without_GitHubs_digest_the_checksum_in_the_notes_is_used()
    {
        var u = UpdateService.Parse(Release(digest: null, notes: $"### Install\n**SHA-256** `x.exe`\n```\n{Hash.ToUpperInvariant()}\n```"));
        Assert.Equal(Hash, u?.Sha256);
    }

    [Fact]
    public void Two_different_checksums_mean_neither_is_trusted()
    {
        var u = UpdateService.Parse(Release(notes: $"**SHA-256**\n```\n{Other}\n```"));
        Assert.NotNull(u);
        Assert.Null(u.Sha256);   // shown, but can't be installed automatically
    }

    [Fact]
    public void No_checksum_at_all_means_it_cant_be_installed_automatically() =>
        Assert.Null(UpdateService.Parse(Release(digest: null, notes: "no hash here"))?.Sha256);

    [Theory]
    [InlineData("v1.0.8", "LlamaTree-Setup-1.0.7.exe")]   // the installer doesn't match the version
    [InlineData("latest", "LlamaTree-Setup-1.0.8.exe")]   // not a version
    public void A_release_without_its_own_installer_or_version_is_not_offered(string tag, string asset) =>
        Assert.Null(UpdateService.Parse(Release(tag: tag, asset: asset)));

    [Fact]
    public void A_prerelease_is_not_offered() => Assert.Null(UpdateService.Parse(Release(prerelease: true)));

    [Theory]
    [InlineData(InstallKind.Deb)]
    [InlineData(InstallKind.Rpm)]
    [InlineData(InstallKind.Tar)]
    public void A_linux_copy_updates_from_the_package_it_was_installed_from(InstallKind kind)
    {
        var name = LinuxUpdate.AssetName(kind, new Version(1, 0, 8))!;
        var json = Release(asset: name).Replace("\"name\":\"notes.txt\"", "\"name\":\"LlamaTree-Setup-1.0.8.exe\"");

        var u = UpdateService.Parse(json, kind);

        Assert.Equal(name, u?.AssetName);
        Assert.True(u?.ForThisMachine);
    }

    [Theory]
    [InlineData(InstallKind.Deb)]
    [InlineData(InstallKind.None)]   // macOS, or a copy run from source
    public void Without_its_package_the_release_is_still_shown_but_not_installed(InstallKind kind)
    {
        var u = UpdateService.Parse(Release(), kind);
        Assert.Equal("LlamaTree-Setup-1.0.8.exe", u?.AssetName);
        Assert.False(u?.ForThisMachine);
    }

    [Fact]
    public void A_mac_copy_updates_from_the_disk_image_for_its_processor()
    {
        var name = MacUpdate.AssetName(new Version(1, 0, 8));
        Assert.Matches(@"^LlamaTree-1\.0\.8-macos-(arm64|x64)\.dmg$", name);
        var json = Release(asset: name).Replace("\"name\":\"notes.txt\"", "\"name\":\"LlamaTree-Setup-1.0.8.exe\"");

        var u = UpdateService.Parse(json, InstallKind.Mac);

        Assert.Equal(name, u?.AssetName);
        Assert.True(u?.ForThisMachine);
    }

    [Fact]
    public void A_release_without_the_mac_disk_image_is_shown_to_a_mac_but_not_installed()
    {
        var u = UpdateService.Parse(Release(), InstallKind.Mac);
        Assert.Equal("LlamaTree-Setup-1.0.8.exe", u?.AssetName);
        Assert.False(u?.ForThisMachine);
    }

    [Theory]
    [InlineData("/Applications/LlamaTree.app/Contents/MacOS/", "/Applications/LlamaTree.app")]
    [InlineData("/Users/me/Applications/LlamaTree 2.app/Contents/MacOS", "/Users/me/Applications/LlamaTree 2.app")]
    [InlineData("/Users/me/src/LlamaTree/bin/Debug/net10.0/", null)]   // run from source
    [InlineData("/Applications/LlamaTree.app/Contents/Resources/", null)]
    [InlineData("/Volumes/x/Contents/MacOS/", null)]                    // not a .app
    public void A_mac_copy_knows_its_app_bundle_from_where_it_runs(string programFolder, string? bundle)
    {
        if (OperatingSystem.IsWindows()) return;   // the paths are Unix ones
        Assert.Equal(bundle, MacUpdate.BundleOf(programFolder));
    }

    [Fact]
    public void The_mac_swap_moves_the_old_app_aside_puts_it_back_if_the_new_one_fails_and_reopens_the_app()
    {
        var finish = MacUpdate.FinishCommands("/Applications/LlamaTree.app", "/Applications/.llamatree-update-1a2b3c4d");
        Assert.Equal(
            "if mv '/Applications/LlamaTree.app' '/Applications/.llamatree-update-1a2b3c4d/previous.app'; " +
            "then mv '/Applications/.llamatree-update-1a2b3c4d/LlamaTree.app' '/Applications/LlamaTree.app' || " +
            "mv '/Applications/.llamatree-update-1a2b3c4d/previous.app' '/Applications/LlamaTree.app'; fi; " +
            "rm -rf '/Applications/.llamatree-update-1a2b3c4d'; exec /usr/bin/open '/Applications/LlamaTree.app'",
            finish.Replace('\\', '/'));
    }

    /// <summary>What macOS 27's diskutil info says of a mounted disk image (41 lines, its disk near the top).</summary>
    private const string DiskutilInfo = """
           Device Identifier:         disk5s1
           Device Node:               /dev/disk5s1
           Whole:                     No
           Part of Whole:             disk5

           Volume Name:               LlamaTree
           Mounted:                   Yes
           Mount Point:               /private/var/folders/_6/sfsg984j4k781dvh350n_0zw0000gn/T/tmp.KAyRLXBg07

           Partition Type:            Apple_HFS
           File System Personality:   HFS+
           Type (Bundle):             hfs
           Name (User Visible):       Mac OS Extended
           Owners:                    Disabled

           OS Can Be Installed:       No
           Media Type:                Generic
           Protocol:                  Disk Image
           SMART Status:              Not Supported
           Volume UUID:               75071EB8-AD45-315E-BC77-6AAE124062A3
           Disk / Partition UUID:     97020B6F-8FDB-4600-8AD9-0A87F25FA256
           Partition Offset:          20480 Bytes (40 512-Byte-Device-Blocks)

           Disk Size:                 133.3 MB (133259264 Bytes) (exactly 260272 512-Byte-Units)
           Device Block Size:         512 Bytes

           Volume Total Space:        133.3 MB (133259264 Bytes) (exactly 260272 512-Byte-Units)
           Volume Used Space:         115.4 MB (115404800 Bytes) (exactly 225400 512-Byte-Units) (86.6%)
           Volume Free Space:         17.9 MB (17854464 Bytes) (exactly 34872 512-Byte-Units) (13.4%)
           Allocation Block Size:     4096 Bytes

           Media OS Use Only:         No
           Media Read-Only:           Yes
           Volume Read-Only:          Yes (read-only mount flag set)

           Device Location:           External
           Removable Media:           Removable
           Media Removal:             Software-Activated

           Solid State:               Yes
        """;

    [Fact]
    public void The_mac_update_ejects_its_disk_image_by_the_whole_disk_diskutil_names()
    {
        // All of diskutil's answer is read: the disk is on its fourth line (the first Mac test kept only the last six,
        // and left the image attached).
        Assert.Equal("disk5", MacUpdate.WholeDisk(DiskutilInfo));
        Assert.Equal("disk5", MacUpdate.WholeDisk(DiskutilInfo.Replace("\n", "\r\n")));
        Assert.Null(MacUpdate.WholeDisk("Could not find disk: /tmp/nothing"));
    }

    [Fact]
    public void The_linux_packages_are_named_as_the_packaging_tool_names_them()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        var v = new Version(1, 2, 3);
        Assert.Equal("llamatree_1.2.3_amd64.deb", LinuxUpdate.AssetName(InstallKind.Deb, v));
        Assert.Equal("llamatree-1.2.3-1.x86_64.rpm", LinuxUpdate.AssetName(InstallKind.Rpm, v));
        Assert.Equal("LlamaTree-1.2.3-linux-x64.tar.gz", LinuxUpdate.AssetName(InstallKind.Tar, v));
        Assert.Null(LinuxUpdate.AssetName(InstallKind.Windows, v));
    }

    [Fact]
    public void The_service_initialises_and_knows_this_copys_version()
    {
        // Touching a static field runs all of the class's static initializers (including the HTTP client's).
        var v = UpdateService.CurrentVersion;
        Assert.NotNull(v);
        Assert.Equal(-1, v.Revision);   // three parts, as release tags have
    }

    [Fact]
    public void Versions_compare_without_a_fourth_part()
    {
        Assert.Equal(new Version(1, 0, 7), UpdateService.Normalize(new Version(1, 0, 7, 0)));
        Assert.True(UpdateService.Normalize(new Version(1, 0, 10)) > UpdateService.Normalize(new Version(1, 0, 9, 0)));
        Assert.Equal(new Version(2, 0, 0), UpdateService.Normalize(new Version(2, 0)));
    }

    [Theory]
    [InlineData("claude", "claude --resume abc-123")]
    [InlineData("claude --model opus", "claude --model opus --resume abc-123")]
    [InlineData("Claude.exe", "Claude.exe --resume abc-123")]
    [InlineData("claude --continue", "claude --resume abc-123")]       // the exact conversation, not just the folder's latest
    [InlineData("claude -c --model opus", "claude --model opus --resume abc-123")]
    [InlineData("claude --resume other", "claude --resume other")]     // already picks its conversation
    [InlineData("claude -p hello", "claude -p hello")]
    [InlineData("pwsh", "pwsh")]                                       // not Claude
    [InlineData("node fake.mjs", "node fake.mjs")]
    public void After_an_update_a_claude_session_resumes_its_conversation(string command, string expected) =>
        Assert.Equal(expected, ClaudeAgent.PrepareCommand(command, "abc-123", () => true));

    [Theory]
    [InlineData(null)]
    [InlineData("abc 123")]
    [InlineData("abc&calc")]
    [InlineData("\"quoted\"")]
    public void Only_a_plain_conversation_id_is_ever_added(string? id) =>
        Assert.Equal("claude", ClaudeAgent.PrepareCommand("claude", id, () => true));

    [Theory]
    [InlineData("claude --continue", true, "claude --continue")]
    [InlineData("claude --continue", false, "claude")]                // nothing to continue yet: start a new one
    [InlineData("claude -c --model opus", false, "claude --model opus")]
    [InlineData("claude --model opus --continue", false, "claude --model opus")]
    [InlineData("claude", false, "claude")]
    [InlineData("claude --resume abc", false, "claude --resume abc")]
    [InlineData("node fake.mjs --continue", false, "node fake.mjs --continue")]   // not Claude
    public void Continue_is_dropped_only_when_the_folder_has_no_conversation(string command, bool has, string expected) =>
        Assert.Equal(expected, ClaudeAgent.PrepareCommand(command, null, () => has));

    [WindowsTheory]
    [InlineData("claude", "claude --settings \"T.json\"")]
    [InlineData("claude --continue", "claude --continue --settings \"T.json\"")]
    [InlineData("claude --resume abc", "claude --resume abc --settings \"T.json\"")]   // a resumed session gets the theme too
    [InlineData("claude --settings mine.json", "claude --settings mine.json")]          // the user's own settings win
    [InlineData("claude --settings=mine.json", "claude --settings=mine.json")]
    [InlineData("claude -p hello", "claude -p hello")]                                  // no interface to theme
    [InlineData("pwsh", "pwsh")]
    public void Claude_is_started_in_the_theme_of_its_terminal(string command, string expected) =>
        Assert.Equal(expected, ClaudeAgent.PrepareCommand(command, null, () => true, "T.json"));

    [WindowsFact]
    public void A_theme_restart_resumes_the_conversation_and_sets_the_theme()
    {
        Assert.Equal("claude --resume abc-123 --settings \"T.json\"", ClaudeAgent.PrepareCommand("claude --continue", "abc-123", () => true, "T.json"));
        Assert.Null(ClaudeAgent.ThemeSettingsFile("solarized"));
        Assert.Null(ClaudeAgent.ThemeSettingsFile(null));
    }

    [Fact]
    public void The_folder_is_only_checked_when_the_command_continues()
    {
        var checkedFolder = false;
        ClaudeAgent.PrepareCommand("claude --model opus", null, () => checkedFolder = true);
        ClaudeAgent.PrepareCommand("pwsh", null, () => checkedFolder = true);
        Assert.False(checkedFolder);
    }
}

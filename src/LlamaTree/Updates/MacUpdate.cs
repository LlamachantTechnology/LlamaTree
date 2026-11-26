using System.Diagnostics;
using System.Runtime.InteropServices;
using LlamaTree.Terminal;

namespace LlamaTree.Updates;

/// <summary>
/// macOS updates. The release's disk image for this Mac (LlamaTree-&lt;version&gt;-macos-&lt;arm64|x64&gt;.dmg) is checked
/// against the signed SHA256SUMS like any update (see <see cref="UpdateService"/>), and then as Gatekeeper would check
/// it, since nothing it downloads is: the image and the app in it must be signed by our Developer ID team and the app
/// notarized by Apple (as tools/install/install.sh checks them). The new LlamaTree.app is copied beside the running one
/// while the app is still open, so a failure can be shown, and the two are swapped once the app and its session host
/// have closed; it then reopens.
/// <para>
/// Only a copy that can be replaced updates itself: an app bundle (not a copy run from source) whose folder this user
/// can write to, which also rules out one run straight from its disk image or from App Translocation's read-only copy.
/// </para>
/// </summary>
internal static class MacUpdate
{
    /// <summary>Our Developer ID team (Llamachant Technology Ltd.), as on the certificate the app is signed with.</summary>
    internal const string Team = "N83JJRC3Y5";

    /// <summary>
    /// What our signature must be, in codesign's terms: Apple's designated requirement for a Developer ID application
    /// (Apple's anchor, the Developer ID intermediate and leaf extensions), issued to our team.
    /// </summary>
    internal const string Requirement =
        "anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and " +
        "certificate leaf[field.1.2.840.113635.100.6.1.13] exists and certificate leaf[subject.OU] = \"" + Team + "\"";

    /// <summary>This copy's LlamaTree.app, or null when it isn't running from an app bundle.</summary>
    public static string? Bundle => BundleOf(AppContext.BaseDirectory);

    /// <summary>The app bundle a program folder is in (…/LlamaTree.app/Contents/MacOS), or null.</summary>
    internal static string? BundleOf(string programFolder)
    {
        var macOS = Path.TrimEndingDirectorySeparator(programFolder);
        if (Path.GetFileName(macOS) != "MacOS") return null;
        var contents = Path.GetDirectoryName(macOS);
        if (contents == null || Path.GetFileName(contents) != "Contents") return null;
        var bundle = Path.GetDirectoryName(contents);
        return bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    public static InstallKind Detect() =>
        Bundle is { } bundle && Path.GetDirectoryName(bundle) is { } folder && Writable(folder) && Writable(bundle) ? InstallKind.Mac : InstallKind.None;

    /// <summary>
    /// The release's disk image for this Mac: Apple silicon's even when this copy is the Intel build running under
    /// Rosetta, so such a copy moves to the native build.
    /// </summary>
    public static string AssetName(Version version) => $"LlamaTree-{version}-macos-{(AppleSilicon() ? "arm64" : "x64")}.dmg";

    private static bool AppleSilicon()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return true;
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            int value = 0;
            var size = (nint)sizeof(int);
            return sysctlbyname("hw.optional.arm64", ref value, ref size, IntPtr.Zero, 0) == 0 && value == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctlbyname(string name, ref int oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    /// <summary>
    /// Checks the downloaded disk image, and copies its LlamaTree.app beside the running one (checked again there).
    /// Returns the shell commands that, once the app has closed, swap the new copy in and reopen it. Throws, saying
    /// why, having left the running copy as it was.
    /// </summary>
    public static async Task<string> PrepareAsync(string image, CancellationToken ct = default)
    {
        var app = Bundle ?? throw new InvalidOperationException("This copy of LlamaTree isn't an app bundle, so it can't update itself.");
        var folder = Path.GetDirectoryName(app)!;

        if (!await SignedByUsAsync(image, deep: false, ct))
            throw new InvalidOperationException($"The downloaded disk image isn't signed by {Publisher.Name}'s Developer ID ({Team}), so nothing was installed.");

        var staging = Path.Combine(folder, ".llamatree-update-" + Guid.NewGuid().ToString("N")[..8]);
        var mount = Path.Combine(Path.GetTempPath(), "llamatree-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        var attached = false;
        try
        {
            await AttachAsync(image, mount, ct);
            attached = true;
            var inImage = Path.Combine(mount, "LlamaTree.app");
            if (!Directory.Exists(inImage)) throw new InvalidOperationException("The downloaded disk image isn't laid out as expected, so nothing was installed.");
            if (!await SignedByUsAsync(inImage, deep: true, ct))
                throw new InvalidOperationException($"The LlamaTree.app in the update isn't signed by {Publisher.Name}'s Developer ID ({Team}), so it wasn't installed.");
            var (accepted, why) = await RunAsync("/usr/sbin/spctl", ["--assess", "--type", "execute", inImage], ct);
            if (!accepted)
                throw new InvalidOperationException($"macOS doesn't accept the LlamaTree.app in the update (it should be notarized by Apple), so it wasn't installed.\n{Tail(why)}");

            Directory.CreateDirectory(staging);
            var copy = Path.Combine(staging, "LlamaTree.app");
            // ditto keeps the signatures, some of which are in extended attributes.
            var (copied, error) = await RunAsync("/usr/bin/ditto", [inImage, copy], ct);
            if (!copied) throw new InvalidOperationException($"The update couldn't be copied beside LlamaTree.\n{Tail(error)}");
            if (!await SignedByUsAsync(copy, deep: true, ct))
                throw new InvalidOperationException("The copied update doesn't check out as the one that was downloaded, so it wasn't installed.");
            return FinishCommands(app, staging);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            if (attached) await DetachAsync(mount);
            try { Directory.Delete(mount); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Swaps <paramref name="app"/> for the copy in <paramref name="staging"/> with two renames (the old one moves aside
    /// first, and comes back if the new one can't take its place), removes the old one, and opens the app.
    /// </summary>
    internal static string FinishCommands(string app, string staging)
    {
        var q = CommandLineResolver.ShellQuote;
        var old = Path.Combine(staging, "previous.app");
        var copy = Path.Combine(staging, "LlamaTree.app");
        return $"if mv {q(app)} {q(old)}; then mv {q(copy)} {q(app)} || mv {q(old)} {q(app)}; fi; " +
               $"rm -rf {q(staging)}; exec /usr/bin/open {q(app)}";
    }

    /// <summary>
    /// Runs <paramref name="finish"/> once <paramref name="processes"/> (the session host and its window) have exited, at
    /// most two minutes on, in a session of its own so nothing that ends the app ends it too.
    /// </summary>
    public static void FinishAfterExit(string finish, int[] processes)
    {
        var script = $"i=0; while {{ {string.Join(" || ", processes.Select(p => $"kill -0 {p} 2>/dev/null"))}; }} && [ $i -lt 600 ]; " +
                     $"do sleep 0.2; i=$((i+1)); done; {finish}";
        ProcessStartInfo psi;
        if (File.Exists("/usr/bin/perl"))
        {
            // macOS has no setsid command; perl's POSIX::setsid does the same.
            psi = new ProcessStartInfo("/usr/bin/perl");
            foreach (var a in new[] { "-MPOSIX", "-e", "POSIX::setsid(); exec @ARGV", "/bin/sh", "-c", script }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            foreach (var a in new[] { "-c", script }) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Whether <paramref name="path"/> (a disk image, or an app with everything in it) is signed by our team.</summary>
    private static async Task<bool> SignedByUsAsync(string path, bool deep, CancellationToken ct)
    {
        string[] args = deep ? ["--verify", "--deep", "--strict", "-R=" + Requirement, path] : ["--verify", "--strict", "-R=" + Requirement, path];
        return (await RunAsync("/usr/bin/codesign", args, ct)).Ok;
    }

    // macOS 27 has diskutil image attach, and calls hdiutil attach and detach deprecated; older versions have only hdiutil.
    private static async Task<bool> HasDiskutilImageAsync(CancellationToken ct) => (await RunAsync("/usr/sbin/diskutil", ["image", "--help"], ct)).Ok;

    private static async Task AttachAsync(string image, string mount, CancellationToken ct)
    {
        var (ok, error) = await HasDiskutilImageAsync(ct)
            ? await RunAsync("/usr/sbin/diskutil", ["image", "attach", "--readOnly", "--nobrowse", "--mountPoint", mount, image], ct)
            : await RunAsync("/usr/bin/hdiutil", ["attach", image, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount], ct);
        if (!ok) throw new InvalidOperationException($"The downloaded disk image couldn't be opened.\n{Tail(error)}");
    }

    /// <summary>Ejects the image at <paramref name="mount"/>; forcibly when something still has a file open in it.</summary>
    private static async Task DetachAsync(string mount)
    {
        if (await HasDiskutilImageAsync(default))
        {
            // By its whole disk: once unmounted, the mount point no longer names it.
            var (_, info) = await RunAsync("/usr/sbin/diskutil", ["info", mount], default);
            var disk = WholeDisk(info) ?? mount;   // not found: by its mount point, which works while it's mounted
            if ((await RunAsync("/usr/sbin/diskutil", ["eject", disk], default)).Ok) return;
            await RunAsync("/usr/sbin/diskutil", ["unmountDisk", "force", disk], default);
            await RunAsync("/usr/sbin/diskutil", ["eject", disk], default);
        }
        else if (!(await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-quiet"], default)).Ok)
        {
            await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-force", "-quiet"], default);
        }
    }

    /// <summary>The whole disk ("disk5") in what <c>diskutil info</c> says of a volume, or null.</summary>
    internal static string? WholeDisk(string info) =>
        info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Part of Whole:", StringComparison.Ordinal))?["Part of Whole:".Length..].Trim()
            is { Length: > 0 } disk ? disk : null;

    /// <summary>Runs a program to the end (at most five minutes): whether it succeeded, and all it printed.</summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string program, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(program)} didn't start.");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var errors = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (false, $"{Path.GetFileName(program)} took too long.");
        }
        return (p.ExitCode == 0, ((await output) + (await errors)).Trim());
    }

    /// <summary>The last few lines of a program's output, for a message.</summary>
    private static string Tail(string output) => string.Join('\n', output.Split('\n').TakeLast(6));

    /// <summary>
    /// Whether this user may change what's in <paramref name="folder"/>, by asking the system (access(2)), not by
    /// writing a file to find out: the app bundle is signed, and a stray file in it would break that.
    /// </summary>
    private static bool Writable(string folder)
    {
        try { return access(folder, 2 /* W_OK */) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int access(string path, int mode);
}

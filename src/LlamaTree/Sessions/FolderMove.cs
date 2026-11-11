using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaTree.Git;
using LlamaTree.Platform;

namespace LlamaTree.Sessions;

/// <summary>
/// Moves a session's folder on disk (Edit session's Folder): a worktree with git worktree move, so its repository keeps
/// track of it; a repository's main folder as a folder, then git worktree repair, so its other worktrees find it again;
/// anything else as a folder. The agents' saved conversations of the folder (and of folders inside it) go with it, so
/// a session there still reopens its conversation.
/// </summary>
internal static class FolderMove
{
    /// <summary>
    /// Why <paramref name="from"/> can't be moved to <paramref name="to"/>, or null if it can. Both full paths; the folder
    /// has to be there, and the new one not: a move never puts anything over what's already there.
    /// </summary>
    public static string? Problem(string from, string to)
    {
        if (!Path.IsPathFullyQualified(to)) return "The new folder must be a full path, such as D:\\Source\\repo-feature.";
        if (!Directory.Exists(from)) return $"{from} isn't there any more, so there's nothing to move.";
        var (fullFrom, fullTo) = (PathText.TrimEnd(Path.GetFullPath(from)), PathText.TrimEnd(Path.GetFullPath(to)));
        if (Path.GetDirectoryName(fullFrom) == null) return "A drive's own folder can't be moved.";
        if (Directory.Exists(fullTo) || File.Exists(fullTo))
            return string.Equals(fullFrom, fullTo, StringComparison.OrdinalIgnoreCase)
                ? "To change only the letter case of a folder's name, rename it outside LlamaTree."
                : $"{fullTo} already exists. Pick a folder that isn't there yet: the session's folder is moved to it.";
        if (Inside(fullTo, fullFrom)) return "A folder can't be moved into itself.";
        if (!string.Equals(Path.GetPathRoot(fullFrom), Path.GetPathRoot(fullTo), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return "The folder can only be moved on the same drive.";
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (full paths, compared as the system does).</summary>
    public static bool Inside(string path, string folder)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var (p, f) = (PathText.TrimEnd(Path.GetFullPath(path)), PathText.TrimEnd(Path.GetFullPath(folder)));
        return string.Equals(p, f, comparison) || p.StartsWith(PathText.WithSeparator(f), comparison);
    }

    /// <summary>Where <paramref name="path"/>, <paramref name="from"/> or inside it, is once <paramref name="from"/> has moved to <paramref name="to"/>.</summary>
    public static string Rebase(string path, string from, string to)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(from), Path.GetFullPath(path));
        return rel == "." ? PathText.TrimEnd(Path.GetFullPath(to)) : Path.Combine(PathText.TrimEnd(Path.GetFullPath(to)), rel);
    }

    /// <summary>
    /// Moves the folder. Something can still be letting go of it (a program that was running in it), so a refused move
    /// is tried again for a few seconds before the reason is given.
    /// </summary>
    public static async Task MoveAsync(string from, string to)
    {
        from = PathText.TrimEnd(Path.GetFullPath(from));
        to = PathText.TrimEnd(Path.GetFullPath(to));
        if (Problem(from, to) is { } problem) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        var worktrees = await GitService.GetWorktreesAsync(from);
        var i = worktrees.FindIndex(w => !w.Bare && PathText.SamePlace(w.Path, from));
        if (i > 0)
        {
            // A linked worktree: git moves it and updates its entry. Run from the main worktree, since a git running
            // inside the folder would itself hold it on Windows.
            var main = worktrees[0].Path;
            if (worktrees[i].Prunable || !Directory.Exists(main)) throw new InvalidOperationException("Git can't find this worktree's repository, so it wasn't moved.");
            await Retry(async () =>
            {
                var r = await GitService.RunAsync(main, ["worktree", "move", "--end-of-options", from, to], timeout: TimeSpan.FromMinutes(2));
                if (!r.Ok) throw new IOException(string.IsNullOrWhiteSpace(r.Error) ? "git couldn't move the worktree." : r.Error.Trim());
            });
            return;
        }

        await Retry(() => { Directory.Move(from, to); return Task.CompletedTask; });
        // A repository's main folder: its other worktrees point back at it, and git worktree repair puts that right.
        if (i == 0 && worktrees.Count(w => !w.Bare) > 1)
        {
            var r = await GitService.RunAsync(to, ["worktree", "repair"]);
            if (!r.Ok) AppLog.Warn($"git worktree repair after moving {from} to {to}: {r.Error.Trim()}");
        }
    }

    private static async Task Retry(Func<Task> move)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await move();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                await Task.Delay(500);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The folder couldn't be moved: {ex.Message.Trim()} Something may still have it open (an editor, a terminal, a build server); close it and try again.", ex);
            }
        }
    }

    // ---------------------------------------------------------------- the agents' conversations

    /// <summary>
    /// Moves Claude's saved conversations of <paramref name="from"/> and of the folders inside it to the folders they're in
    /// now (~/.claude/projects/&lt;encoded folder&gt;, see <see cref="ConversationIndex"/>), with the folder each was had in
    /// (cwd) updated, so claude --resume finds them there and LlamaTree lists them for the new folder. A conversation already
    /// saved under the new folder's name is left as it is.
    /// </summary>
    public static void MoveClaudeConversations(string from, string to)
    {
        var root = ConversationIndex.ProjectsRoot;
        if (!Directory.Exists(root)) return;
        var encoded = ConversationIndex.Encode(from);
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            var name = Path.GetFileName(dir);
            if (!name.Equals(encoded, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(encoded + "-", StringComparison.OrdinalIgnoreCase)) continue;
            // Names are lossy (every non-letter is a '-'), so the folder a conversation was really had in decides.
            var cwd = name.Equals(encoded, StringComparison.OrdinalIgnoreCase) ? from : ClaudeFolderOf(dir);
            if (cwd == null || !Inside(cwd, from)) continue;
            var target = Path.Combine(root, ConversationIndex.Encode(Rebase(cwd, from, to)));
            try
            {
                Merge(dir, target);
                foreach (var file in Directory.EnumerateFiles(target, "*.jsonl", SearchOption.AllDirectories))
                    RewriteCwd(file, from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Moving Claude's conversations from {dir} to {target}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Updates the folder in Codex's saved conversations of <paramref name="from"/> and the folders inside it, so they're
    /// listed (and resumed) as the new folder's.
    /// </summary>
    public static void MoveCodexConversations(string from, string to)
    {
        foreach (var file in CodexConversations.RolloutsIn(from))
        {
            try
            {
                RewriteCwd(file, from, to);
                CodexConversations.Forget(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Moving the Codex conversation {file} to {to}: {ex.Message}");
            }
        }
    }

    /// <summary>The folder a Claude project folder's conversations were had in: the first cwd in its latest transcript.</summary>
    private static string? ClaudeFolderOf(string projectDir)
    {
        try
        {
            foreach (var file in new DirectoryInfo(projectDir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                for (var n = 0; n < 50 && reader.ReadLine() is { } line; n++)
                {
                    if (line.Length < 2 || line[0] != '{' || !line.Contains("\"cwd\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Moves the folder's contents into <paramref name="target"/>, leaving anything of the same name already there.</summary>
    private static void Merge(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
        {
            var dest = Path.Combine(target, Path.GetFileName(entry));
            if (File.Exists(dest) || Directory.Exists(dest)) continue;
            if (Directory.Exists(entry)) Directory.Move(entry, dest);
            else File.Move(entry, dest);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    /// <summary>
    /// Every "cwd" in the JSON lines file that is <paramref name="from"/> or inside it made the same place under
    /// <paramref name="to"/>. Written beside it and swapped in, and only when something changed.
    /// </summary>
    internal static void RewriteCwd(string file, string from, string to)
    {
        var pattern = CwdPattern(from);
        var replacement = "$1" + JsonText(PathText.TrimEnd(Path.GetFullPath(to))).Replace("$", "$$");
        var temp = file + ".llamatree-move";
        var changed = false;
        using (var input = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8))
        using (var output = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            // Lines are copied with "\n" whatever they ended with; the agents write "\n".
            while (input.ReadLine() is { } line)
            {
                var updated = line.Contains("\"cwd\"", StringComparison.Ordinal) ? pattern.Replace(line, replacement) : line;
                changed |= !ReferenceEquals(updated, line);   // Replace gives the same string back when nothing matched
                output.Write(updated);
                output.Write('\n');
            }
        }
        if (changed) File.Move(temp, file, overwrite: true);
        else File.Delete(temp);
    }

    /// <summary>"cwd":"&lt;from&gt;" with nothing or a separator after it, the folder as JSON writes it (backslashes doubled).</summary>
    private static Regex CwdPattern(string from)
    {
        var text = Regex.Escape(JsonText(PathText.TrimEnd(Path.GetFullPath(from))));
        var options = OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex("(\"cwd\"\\s*:\\s*\")" + text + "(?=\"|\\\\\\\\|/)", options | RegexOptions.CultureInvariant);
    }

    /// <summary>The path as it appears inside a JSON string.</summary>
    private static string JsonText(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

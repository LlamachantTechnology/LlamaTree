using System.Text.RegularExpressions;
using LlamaTree.Platform;

namespace LlamaTree.Git;

/// <summary>
/// One line of work not on the base branch yet: a local branch with commits the base doesn't have, or uncommitted
/// changes in the worktree it's checked out in.
/// </summary>
/// <param name="Ahead">Commits on the branch the base doesn't have.</param>
/// <param name="Behind">Commits on the base the branch doesn't have.</param>
/// <param name="Uncommitted">Files changed or new in its worktree, not committed yet (0 without a worktree).</param>
/// <param name="Unpushed">Commits not on its upstream yet; 0 without one (see <paramref name="Upstream"/>).</param>
/// <param name="Files">Files its commits change since it left the base.</param>
/// <param name="BaseConflicts">Files that would conflict merging it into the base: empty when it merges cleanly, null when not checked.</param>
/// <param name="Remote">Only on a remote (origin/x, say), with no local branch of the same name: someone else's, often.</param>
internal sealed record InFlightBranch(string Name, string? Worktree, int Ahead, int Behind, int Uncommitted, string? Upstream,
    int Unpushed, string Subject, string Date, int Files, IReadOnlyList<string>? BaseConflicts, bool Remote = false);

/// <summary>Two open branches changing the same files.</summary>
/// <param name="Files">The files both change, committed or not (the first <see cref="InFlightService.MaxFilesListed"/>).</param>
/// <param name="Conflicts">Files that would conflict merging one into the other: empty when they merge cleanly; null
/// when not checked (conflict checks off, or the overlap is only in uncommitted changes).</param>
internal sealed record InFlightOverlap(string A, string B, int Count, IReadOnlyList<string> Files, IReadOnlyList<string>? Conflicts);

internal sealed record LandedCommit(string Sha, string Subject, string Author, string Date);

/// <param name="ConflictCheck">"on"; "untrusted" when the repository's own settings could run a program during a merge
/// (a merge driver) and it isn't trusted; "unsupported" when git is older than 2.38 (no merge-tree --write-tree).</param>
/// <param name="Landed">Commits that reached the base since the commit asked about, newest first; null when not asked,
/// or when that commit isn't behind the base any more (the base was rewritten).</param>
/// <param name="Repo">The repository's main worktree, the same from every worktree of it: what the page remembers "since
/// you last looked" by.</param>
internal sealed record InFlightReport(string Repo, string? BaseBranch, string? BaseTip, string ConflictCheck, List<InFlightBranch> Branches,
    List<InFlightOverlap> Overlaps, List<LandedCommit>? Landed);

/// <summary>
/// Git Repository's In flight view: what's being worked on beside the base branch, and where it will collide. Each open
/// branch is compared with the base and with every other open branch. Branches that change the same files are tried
/// against each other with git merge-tree, which merges in memory: it touches no worktree, index or branch, and only
/// adds objects git cleans up on its own. Reads only otherwise.
/// </summary>
internal static class InFlightService
{
    /// <summary>At most this many branches, the most recently committed to, so the pairwise checks stay quick.</summary>
    public const int MaxBranches = 30;
    public const int MaxFilesListed = 50;
    private const int MaxLanded = 30;
    private const int Parallel = 4;

    private sealed record Work(InFlightBranch Info, string Ref, HashSet<string> Committed, HashSet<string> Dirty)
    {
        public IEnumerable<string> All => Committed.Concat(Dirty);
    }

    /// <summary>Failed: git couldn't merge them at all (no common history, say); Unsupported: git has no merge-tree --write-tree.</summary>
    private enum Merge { Clean, Conflicts, Failed, Unsupported }

    private static readonly Regex Sha = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);

    /// <param name="since">The base's commit when the person last looked: what has landed since is listed.</param>
    /// <param name="seen">Instead of <paramref name="since"/>: that commit for each repository the page has looked at, by
    /// its main worktree's path; the one for this repository is used.</param>
    public static async Task<InFlightReport> GetAsync(string dir, string? since = null, IReadOnlyDictionary<string, string>? seen = null)
    {
        var here = await GitService.GetTopLevelAsync(dir) ?? dir;
        var worktrees = await GitService.GetWorktreesAsync(here);
        var repo = worktrees.Count > 0 ? worktrees[0].Path : here;
        since ??= seen?.FirstOrDefault(kv => PathText.SamePlace(kv.Key, repo)).Value;
        var baseName = await GitService.GetBaseBranchAsync(here);
        var baseTip = baseName == null ? null : await ResolveAsync(here, baseName);
        if (baseName == null || baseTip == null) return new(repo, baseName, null, "on", [], [], null);

        // Merging runs any merge driver the repository's own config names, so an untrusted one gets no conflict checks.
        string check;
        try { check = await RepoTrust.RefusalAsync(here, "check for conflicts") == null ? "on" : "untrusted"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { check = "untrusted"; }

        // Local branches, and remote ones nobody has here (a teammate's, say): not a twin of a local one, nor the base itself.
        var all = await GitService.GetBranchesAsync(here);
        var localNames = all.Where(b => !b.Remote).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = all.Where(b => !b.Remote
            || b.Name != baseName && !localNames.Contains(b.Name[(b.Name.IndexOf('/') + 1)..])).ToList();
        var read = await Throttled(candidates, b => ReadAsync(here, baseTip, b));
        var open = read.OfType<Work>().OrderByDescending(w => w.Info.Date, StringComparer.Ordinal).Take(MaxBranches).ToList();

        var unsupported = false;
        async Task<IReadOnlyList<string>?> TryMerge(string a, string b)
        {
            if (check != "on" || unsupported) return null;
            var (result, files) = await MergeTreeAsync(here, a, b);
            if (result == Merge.Unsupported) { unsupported = true; return null; }
            return result == Merge.Failed ? null : files;
        }

        // Against the base: one that is behind nothing merges cleanly without asking.
        var branches = new List<InFlightBranch>();
        foreach (var w in open)
        {
            var i = w.Info;
            IReadOnlyList<string>? conflicts = null;
            if (i.Ahead > 0 && i.Behind == 0 && check == "on") conflicts = [];
            else if (i.Ahead > 0 && i.Behind > 0) conflicts = await TryMerge(baseTip, w.Ref);
            branches.Add(i with { BaseConflicts = conflicts });
        }

        // Against each other: only branches that change the same files can conflict, so only those are merged.
        var pairs = new List<(Work A, Work B, List<string> Files)>();
        for (var a = 0; a < open.Count; a++)
            for (var b = a + 1; b < open.Count; b++)
            {
                var shared = open[a].All.Intersect(open[b].All, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (shared.Count > 0) pairs.Add((open[a], open[b], shared));
            }
        var overlaps = new List<InFlightOverlap>();
        foreach (var (a, b, files) in pairs)
        {
            var committedBoth = a.Committed.Overlaps(b.Committed);
            var conflicts = committedBoth ? await TryMerge(a.Ref, b.Ref) : null;
            overlaps.Add(new(a.Info.Name, b.Info.Name, files.Count, files.Take(MaxFilesListed).ToList(), conflicts));
        }
        if (unsupported)
        {
            check = "unsupported";
            branches = branches.Select(x => x with { BaseConflicts = null }).ToList();
            overlaps = overlaps.Select(x => x with { Conflicts = null }).ToList();
        }

        return new(repo, baseName, baseTip, check, branches, overlaps, await LandedAsync(here, since, baseTip));
    }

    /// <summary>The branch as a line of work, or null when it has nothing the base doesn't (no commits of its own, nothing uncommitted).</summary>
    private static async Task<Work?> ReadAsync(string repo, string baseTip, BranchInfo b)
    {
        var refName = (b.Remote ? "refs/remotes/" : "refs/heads/") + b.Name;
        var (ahead, behind) = await GitService.AheadBehindAsync(repo, baseTip, refName);
        var worktree = b.Worktree != null && Directory.Exists(b.Worktree) ? b.Worktree : null;
        var dirty = worktree != null ? await UncommittedAsync(worktree) : [];
        if (ahead == 0 && dirty.Count == 0) return null;

        var committed = new HashSet<string>(StringComparer.Ordinal);
        if (ahead > 0)
        {
            var diff = await GitService.RunAsync(repo, ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", "--end-of-options", baseTip + "..." + refName, "--"],
                maxOutputBytes: 4 << 20);
            if (diff.Ok) committed.UnionWith(diff.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        var last = await GitService.RunAsync(repo, ["log", "-1", "--format=%s%x1f%aI", "--end-of-options", refName, "--"]);
        var f = last.Ok ? last.Text.TrimEnd('\n', '\r').Split('\x1f') : [];
        var info = new InFlightBranch(b.Name, worktree, ahead, behind, dirty.Count, b.Upstream, b.Upstream != null ? b.Ahead : 0,
            f.Length > 0 ? f[0] : "", f.Length > 1 ? f[1] : "", committed.Count, null, b.Remote);
        return new Work(info, refName, committed, dirty);
    }

    /// <summary>The paths git status lists in a worktree (staged, unstaged and untracked); empty if it couldn't say.</summary>
    private static async Task<HashSet<string>> UncommittedAsync(string worktree)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var r = await GitService.RunAsync(worktree, ["status", "--porcelain", "-z", "--untracked-files=normal"], maxOutputBytes: 4 << 20);
        if (!r.Ok || r.Truncated) return set;
        var entries = r.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var k = 0; k < entries.Length; k++)
        {
            var e = entries[k];
            if (e.Length < 4) continue;
            set.Add(e[3..]);
            if (e[0] is 'R' or 'C') k++;   // a rename or copy is followed by the path it came from
        }
        return set;
    }

    /// <summary>Merges <paramref name="a"/> and <paramref name="b"/> in memory; the files that would conflict.</summary>
    private static async Task<(Merge, IReadOnlyList<string>)> MergeTreeAsync(string repo, string a, string b)
    {
        var r = await GitService.RunAsync(repo, ["merge-tree", "--write-tree", "--name-only", "--no-messages", a, b], maxOutputBytes: 1 << 20,
            timeout: TimeSpan.FromSeconds(30));
        if (r.ExitCode == 0) return (Merge.Clean, []);
        if (r.ExitCode == 129) return (Merge.Unsupported, []);   // git's usage error: --write-tree came in git 2.38
        if (r.ExitCode != 1) return (Merge.Failed, []);
        // The new tree's id, then each conflicted file, then a blank line before any messages.
        var files = r.Text.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).TakeWhile(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return (Merge.Conflicts, files);
    }

    /// <summary>Commits on the base since <paramref name="since"/>, newest first, following its own line (each merge once).</summary>
    private static async Task<List<LandedCommit>?> LandedAsync(string repo, string? since, string baseTip)
    {
        if (since == null || !Sha.IsMatch(since)) return null;
        if (since == baseTip) return [];
        if (!(await GitService.RunAsync(repo, ["merge-base", "--is-ancestor", since, baseTip])).Ok) return null;
        var r = await GitService.RunAsync(repo, ["log", "--first-parent", $"-n{MaxLanded}", "--format=%H%x1f%s%x1f%an%x1f%aI%x1e", since + ".." + baseTip, "--"]);
        if (!r.Ok) return null;
        return r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('\n', '\r').Split('\x1f'))
            .Where(f => f.Length >= 4).Select(f => new LandedCommit(f[0], f[1], f[2], f[3])).ToList();
    }

    private static async Task<string?> ResolveAsync(string repo, string rev)
    {
        var r = await GitService.RunAsync(repo, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"]);
        return r.Ok ? r.Text.Trim() : null;
    }

    private static async Task<T[]> Throttled<TIn, T>(IEnumerable<TIn> items, Func<TIn, Task<T>> work)
    {
        using var gate = new SemaphoreSlim(Parallel);
        return await Task.WhenAll(items.Select(async x =>
        {
            await gate.WaitAsync();
            try { return await work(x); }
            finally { gate.Release(); }
        }));
    }
}

using System.Text.RegularExpressions;

namespace LlamaTree.Git;

/// <summary>A commit on the base's own line (its first parents).</summary>
/// <param name="Files">The files it changes; a merge, everything it brought in (the first <see cref="MainLineService.MaxFiles"/>).</param>
internal sealed record MainCommit(string Sha, string Author, string Date, string Subject, IReadOnlyList<string> Parents, IReadOnlyList<string> Files);

/// <summary>A branch not merged into the base yet, and the base commit it started from.</summary>
/// <param name="Ahead">Its commits the base doesn't have (null with git before 2.41, which can't count them in one call).</param>
internal sealed record OpenBranch(string Name, string Ref, string Tip, string? Fork, string Date, string Subject, int? Ahead = null);

/// <param name="More">The base's line goes on beyond <paramref name="Commits"/>.</param>
/// <param name="Refs">Every branch, remote branch and tag by the commit it points at (a tag's commit, not the tag object), for
/// the badges on whichever rows show those commits.</param>
internal sealed record MainLine(string? BaseBranch, string? BaseTip, List<MainCommit> Commits, bool More, List<OpenBranch> Open,
    Dictionary<string, List<string>>? Refs = null);

/// <summary>
/// Git Repository's Main graph: the base branch's own line, one commit per merge, which the page drills into branch by
/// branch; and the branches still open beside it, with where each started. Reads only.
/// </summary>
internal static class MainLineService
{
    public const int MaxFiles = 100, MaxCommits = 5000, MaxOpen = 40, ContextBefore = 20;

    private static readonly Regex Sha = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);

    /// <param name="count">How many of the base's commits, newest first.</param>
    /// <param name="include">Commits the list must reach (where expanded branches forked or landed), with a few beyond.</param>
    public static async Task<MainLine> GetAsync(string dir, int count, IReadOnlyCollection<string> include)
    {
        var baseName = await GitService.GetBaseBranchAsync(dir);
        var baseTipResult = baseName == null ? null
            : await GitService.RunAsync(dir, ["rev-parse", "--verify", "--quiet", "--end-of-options", baseName + "^{commit}"]);
        var baseTip = baseTipResult is { Ok: true } ? baseTipResult.Text.Trim() : null;
        if (baseTip == null) return new(baseName, null, [], false, []);

        var openTask = OpenAsync(dir, baseTip);
        var refsTask = RefsAsync(dir);
        var wanted = include.Where(x => Sha.IsMatch(x)).ToHashSet(StringComparer.Ordinal);
        count = Math.Clamp(count, 1, MaxCommits);
        var n = count;
        List<MainCommit> commits;
        while (true)
        {
            commits = await ReadAsync(dir, baseTip, n + 1);
            var reached = commits.FindLastIndex(c => wanted.Contains(c.Sha));
            var missing = wanted.Any(w => !commits.Exists(c => c.Sha == w));
            // Every commit asked for, or the whole line read: cut to what's wanted and a little beyond the oldest of them.
            if (!missing || commits.Count <= n || n >= MaxCommits)
            {
                var keep = Math.Max(count, reached < 0 ? 0 : reached + 1 + ContextBefore);
                var more = commits.Count > keep;
                return new(baseName, baseTip, commits.Take(keep).ToList(), more, await openTask, await refsTask);
            }
            n = Math.Min(MaxCommits, n * 3);
        }
    }

    private static async Task<List<MainCommit>> ReadAsync(string dir, string baseTip, int n)
    {
        // -m with --first-parent: a merge's files are what it brought in, as against the line it's on.
        var r = await GitService.RunAsync(dir, ["log", "--first-parent", "-m", "--name-only", $"-n{n}",
            "--format=%x1e%H%x1f%an%x1f%aI%x1f%s%x1f%P%x1f", "--end-of-options", baseTip, "--"], maxOutputBytes: 64 << 20);
        var list = new List<MainCommit>();
        foreach (var rec in r.Ok ? r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = rec.Split('\x1f');
            if (f.Length < 6) continue;
            var names = f[5].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxFiles).ToList();
            list.Add(new(f[0], f[1], f[2], f[3], f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries), names));
        }
        return list;
    }

    /// <summary>Every branch, remote branch and tag by its commit (an annotated tag by the commit it tags), from one call.</summary>
    private static async Task<Dictionary<string, List<string>>> RefsAsync(string dir)
    {
        var r = await GitService.RunAsync(dir, ["for-each-ref", "--format=%(objectname) %(*objectname) %(refname)", "refs/heads", "refs/remotes", "refs/tags"],
            maxOutputBytes: 16 << 20);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            // "<object> <peeled or empty> <ref>": a lightweight ref leaves the middle empty, so there are two spaces.
            var f = line.TrimEnd('\r').Split(' ', 3);
            if (f.Length < 3 || f[2].EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var sha = f[1].Length > 0 ? f[1] : f[0];
            if (!Sha.IsMatch(sha)) continue;
            if (!map.TryGetValue(sha, out var list)) map[sha] = list = [];
            list.Add(f[2]);
        }
        return map;
    }

    /// <summary>
    /// Branches not merged into the base, newest first (the first <see cref="MaxOpen"/>): local ones, and remote ones with
    /// no local branch of the same name. Each with the base commit it started from.
    /// </summary>
    private static async Task<List<OpenBranch>> OpenAsync(string dir, string baseTip)
    {
        // The count before the subject, so the subject (which may hold tabs) stays last. Git before 2.41 has no
        // ahead-behind, so it's asked again without.
        string[] Args(string count) => ["for-each-ref", "--no-merged=" + baseTip, "--sort=-committerdate",
            $"--format=%(refname)%09%(objectname)%09%(committerdate:iso-strict)%09{count}%09%(subject)", "refs/heads", "refs/remotes"];
        var r = await GitService.RunAsync(dir, Args($"%(ahead-behind:{baseTip})"));
        if (!r.Ok) r = await GitService.RunAsync(dir, Args(""));
        var refs = (r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
            .Select(l => l.TrimEnd('\r').Split('\t', 5)).Where(p => p.Length == 5 && !p[0].EndsWith("/HEAD"))
            .Select(p => (Ref: p[0], Local: p[0].StartsWith("refs/heads/"), Name: p[0].StartsWith("refs/heads/") ? p[0]["refs/heads/".Length..] : p[0]["refs/remotes/".Length..],
                Tip: p[1], Date: p[2], Ahead: int.TryParse(p[3].Split(' ')[0], out var a) ? a : (int?)null, Subject: p[4]))
            .ToList();
        var locals = refs.Where(x => x.Local).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var shown = refs.Where(x => x.Local || !locals.Contains(x.Name[(x.Name.IndexOf('/') + 1)..])).Take(MaxOpen).ToList();
        using var gate = new SemaphoreSlim(6);
        var forks = await Task.WhenAll(shown.Select(async x =>
        {
            await gate.WaitAsync();
            try
            {
                var mb = await GitService.RunAsync(dir, ["merge-base", "--end-of-options", baseTip, x.Tip]);
                return mb.Ok ? mb.Text.Trim() : null;
            }
            finally { gate.Release(); }
        }));
        return shown.Select((x, i) => new OpenBranch(x.Name, x.Ref, x.Tip, forks[i], x.Date, x.Subject, x.Ahead)).ToList();
    }
}

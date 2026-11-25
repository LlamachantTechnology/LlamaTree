using System.Text.RegularExpressions;

namespace LlamaTree.Git;

/// <summary>A commit brought into a branch by a merge (main's, taken in to keep up), for its folded row.</summary>
internal sealed record FocusTaken(string Sha, string Subject, string Author, string Date);

/// <summary>One commit on a branch's own line.</summary>
/// <param name="Parents">Its parents, the first on the branch's line.</param>
/// <param name="FromBase">A merge that took in the base's commits (main merged into the branch to keep up).</param>
/// <param name="TookIn">How many commits a merge brought in (0 for an ordinary commit).</param>
/// <param name="Taken">For a merge from the base, the commits it brought in (the first <see cref="BranchFocusService.MaxTaken"/>).</param>
internal sealed record FocusCommit(string Sha, string Author, string Date, string Subject, IReadOnlyList<string> Parents,
    bool FromBase, int TookIn, IReadOnlyList<FocusTaken>? Taken);

/// <summary>Where the branch came from or went: a commit on the base.</summary>
internal sealed record FocusAnchor(string Sha, string Subject, string Date);

/// <param name="Tip">The branch's newest commit.</param>
/// <param name="Landed">The merge that brought the branch into the base; null while it's open.</param>
/// <param name="Fork">The base commit the branch started from: the first parent of its oldest commit.</param>
/// <param name="Commits">Its own line, newest first: following first parents from the tip until the base, so merges into
/// it show as one commit each and the base's history doesn't.</param>
/// <param name="DiffFrom">What its net change is measured from: where it last met the base (git diff base...tip).</param>
/// <param name="Files">Its net change: every file it changes, compared with <paramref name="DiffFrom"/>.</param>
/// <param name="Ahead">Commits on it the base doesn't have (open branches; 0 once landed).</param>
/// <param name="Behind">Commits on the base it doesn't have (open branches).</param>
/// <param name="Truncated">More commits than <see cref="BranchFocusService.MaxCommits"/>; the oldest are left out.</param>
internal sealed record BranchFocus(string? BaseBranch, string Tip, FocusAnchor? Landed, FocusAnchor? Fork, List<FocusCommit> Commits,
    string DiffFrom, List<FileChange> Files, int Ahead, int Behind, bool Truncated);

/// <summary>
/// One branch's own line, for expanding it in Git Repository's Main graph: its commits without the base's, where it forked
/// and where it landed, and its net change. Works for an open branch (by its ref), one merged and deleted long ago (by
/// the merge that brought it in, into the base or into another branch), or just a name. Reads only.
/// </summary>
internal static class BranchFocusService
{
    public const int MaxCommits = 500;
    public const int MaxTaken = 30;

    private static readonly Regex Sha = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);

    /// <param name="refName">A branch by its full name (refs/heads/x, refs/remotes/origin/x).</param>
    /// <param name="merge">Or a merge commit: the branch is its second parent, landed by that merge.</param>
    /// <param name="name">Or just a name: a local branch, origin's, or failing those the newest merge into the base that
    /// names it (however long ago, beyond the history the graph has loaded).</param>
    public static async Task<BranchFocus?> GetAsync(string dir, string? refName, string? merge, string? name = null)
    {
        var baseName = await GitService.GetBaseBranchAsync(dir);
        var baseTip = baseName == null ? null : await ResolveAsync(dir, baseName);
        if (refName == null && merge == null && !string.IsNullOrWhiteSpace(name))
            (refName, merge) = await FindByNameAsync(dir, name.Trim(), baseTip);

        string? tip;
        FocusAnchor? landed = null;
        string? boundary;   // the base as it was when the branch's line ends: commits it has aren't the branch's own
        if (merge != null)
        {
            if (!Sha.IsMatch(merge)) return null;
            var parents = await ParentsAsync(dir, merge);
            if (parents is not { Count: >= 2 }) return null;
            tip = parents[1];
            boundary = parents[0];
            landed = await AnchorAsync(dir, merge);
        }
        else
        {
            if (refName == null || !(refName.StartsWith("refs/heads/") || refName.StartsWith("refs/remotes/"))) return null;
            tip = await ResolveAsync(dir, refName);
            if (tip == null) return null;
            boundary = baseTip;
            // Already in the base (merged but not deleted): find the merge that brought it in, and stop there.
            if (baseTip != null && tip != baseTip && await IsAncestorAsync(dir, tip, baseTip))
            {
                var path = await GitService.RunAsync(dir, ["rev-list", "--ancestry-path", "--merges", "--reverse", "--end-of-options", tip + ".." + baseTip, "--"]);
                foreach (var m in path.Ok ? path.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
                {
                    var ps = await ParentsAsync(dir, m.Trim());
                    if (ps is { Count: >= 2 } && await IsAncestorAsync(dir, tip, ps[1]) && !await IsAncestorAsync(dir, tip, ps[0]))
                    {
                        landed = await AnchorAsync(dir, m.Trim());
                        boundary = ps[0];
                        break;
                    }
                }
            }
        }

       
        // The branch's own line: first parents from its tip, leaving out what the base already had.
        List<string> args = ["log", "--first-parent", $"-n{MaxCommits + 1}", "--format=%H%x1f%an%x1f%aI%x1f%s%x1f%P%x1e", "--end-of-options",
            boundary != null && boundary != tip ? $"{boundary}..{tip}" : tip, "--"];
        if (boundary == null || boundary == tip) args.Insert(2, "-n50");   // the base itself, or no base: its recent line only
        var r = await GitService.RunAsync(dir, args, maxOutputBytes: 8 << 20);
        var commits = new List<FocusCommit>();
        foreach (var rec in r.Ok ? r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 5) continue;
            commits.Add(new(f[0], f[1], f[2], f[3], f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries), false, 0, null));
        }
        var truncated = commits.Count > MaxCommits;
        if (truncated) commits.RemoveAt(commits.Count - 1);

        // Merges into the line: how much each brought in, and whether it was the base (taken in to keep up). What the
        // base had by the end is read once, as one set, rather than asking git about each merge.
        // Each git call costs tens of milliseconds just to start, so the ones that don't wait on each other run together.
       
        var merges = commits.Select((c, i) => (c, i)).Where(x => x.c.Parents.Count >= 2).ToList();
        var oldest = commits.Count > 0 ? commits[^1] : null;
        var inBaseTask = merges.Count > 0 && boundary != null && oldest is { Parents.Count: > 0 }
            ? GitService.RunAsync(dir, ["rev-list", "--end-of-options", boundary, "^" + oldest.Parents[0], "--"], maxOutputBytes: 32 << 20)
            : Task.FromResult<GitResult?>(null)!;
        var countedTask = Throttled(merges, x => TakenAsync(dir, $"{x.c.Parents[0]}..{x.c.Parents[1]}"));
        var forkTask = !truncated && oldest is { Parents.Count: > 0 } ? AnchorAsync(dir, oldest.Parents[0]) : Task.FromResult<FocusAnchor?>(null);
        // Its net change: from where it last met the base (a merge from the base moves that up) to its tip.
        var filesTask = Task.Run(async () =>
        {
            var from = boundary != null ? await MergeBaseAsync(dir, boundary, tip) ?? boundary : oldest?.Parents.FirstOrDefault() ?? tip;
            return (From: from, Files: await NameStatusAsync(dir, from, tip));
        });
        var aheadTask = landed == null && baseTip != null ? GitService.AheadBehindAsync(dir, baseTip, tip) : Task.FromResult((0, 0));

        HashSet<string> inBase = new(StringComparer.Ordinal);
        if (await inBaseTask is { Ok: true } b)
        {
            inBase.UnionWith(b.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            inBase.Add(oldest!.Parents[0]);
        }
        var counted = await countedTask;
        for (var k = 0; k < merges.Count; k++)
        {
            var (c, i) = merges[k];
            var fromBase = inBase.Contains(c.Parents[1]);
            commits[i] = c with { FromBase = fromBase, TookIn = counted[k].Count, Taken = fromBase ? counted[k].First : null };
        }
        var fork = await forkTask;
        var (diffFrom, files) = await filesTask;
        var (ahead, behind) = await aheadTask;
        return new(baseName, tip, landed, fork, commits, diffFrom, files, ahead, behind, truncated);
    }

    public const int MaxMany = 60;

    /// <summary>
    /// <see cref="GetAsync"/> for many branches at once (Expand all), as one result per ref that exists. Each git call costs
    /// tens of milliseconds just to start, and on Windows starting them barely overlaps, so this asks git a handful of
    /// times in all rather than about ten times a branch: the tips and ahead/behind from one for-each-ref, every commit
    /// the branches have that the base doesn't from one log (their lines, and where they meet the base, are worked out from
    /// it), the forks from one log, and every branch's files from one diff-tree. A ref already in the base is asked about
    /// on its own, to find the merge that landed it.
    /// </summary>
    public static async Task<Dictionary<string, BranchFocus>> GetManyAsync(string dir, IEnumerable<string> refNames)
    {
        var result = new Dictionary<string, BranchFocus>(StringComparer.Ordinal);
        var wanted = refNames.Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal) || r.StartsWith("refs/remotes/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(MaxMany).ToList();
        if (wanted.Count == 0) return result;
        var baseName = await GitService.GetBaseBranchAsync(dir);
        var baseTip = baseName == null ? null : await ResolveAsync(dir, baseName);
        var single = new List<string>();   // asked about one by one
        var open = new List<(string Ref, string Tip, int Ahead, int Behind, bool Counted)>();
        if (baseTip == null) single.AddRange(wanted);
        else
        {
            // Patterns match a ref and everything under it, so only the exact names are kept.
            var exact = wanted.ToHashSet(StringComparer.Ordinal);
            var refs = await GitService.RunAsync(dir, ["for-each-ref", $"--format=%(refname) %(objectname) %(ahead-behind:{baseTip})", .. wanted]);
            var counted = refs.Ok;
            if (!counted) refs = await GitService.RunAsync(dir, ["for-each-ref", "--format=%(refname) %(objectname)", .. wanted]);   // git before 2.41
            foreach (var line in refs.Ok ? refs.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
            {
                var f = line.Trim().Split(' ');
                if (f.Length < 2 || !exact.Contains(f[0]) || !Sha.IsMatch(f[1])) continue;
                int ahead = 0, behind = 0;
                if (counted && !(f.Length >= 4 && int.TryParse(f[2], out ahead) && int.TryParse(f[3], out behind))) continue;
                // Nothing the base lacks: already merged, or the base itself. The single path finds where it landed.
                if (counted && ahead == 0) single.Add(f[0]);
                else open.Add((f[0], f[1], ahead, behind, counted));
            }
        }

        var many = open.Count > 0 ? await OpenManyAsync(dir, baseName, baseTip!, open) : [];
        foreach (var (r, focus) in many) result[r] = focus;
        var rest = await Throttled(single, r => GetAsync(dir, r, null));
        for (var i = 0; i < single.Count; i++) if (rest[i] is { } focus) result[single[i]] = focus;
        return result;
    }

    private sealed record RawCommit(string Sha, string Author, string Date, string Subject, string[] Parents);

    private static async Task<List<(string Ref, BranchFocus Focus)>> OpenManyAsync(string dir, string? baseName, string baseTip,
        List<(string Ref, string Tip, int Ahead, int Behind, bool Counted)> open)
    {
        // Every commit some branch has that the base doesn't, with its parents.
        var tips = open.Select(b => b.Tip).Distinct(StringComparer.Ordinal).ToList();
        var log = await GitService.RunAsync(dir, ["log", "--format=%H%x1f%an%x1f%aI%x1f%s%x1f%P%x1e", "--end-of-options", .. tips, "^" + baseTip, "--"],
            maxOutputBytes: 64 << 20);
        var own = new Dictionary<string, RawCommit>(StringComparer.Ordinal);
        foreach (var rec in log.Ok ? log.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length >= 5) own[f[0]] = new(f[0], f[1], f[2], f[3], f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var branches = open.Select(b =>
        {
            // Its line: first parents from the tip until the base has the commit (as git log --first-parent base..tip).
            var line = new List<RawCommit>();
            for (var sha = b.Tip; own.TryGetValue(sha, out var c) && line.Count <= MaxCommits;)
            {
                line.Add(c);
                if (c.Parents.Length == 0) break;
                sha = c.Parents[0];
            }
            var truncated = line.Count > MaxCommits;
            if (truncated) line.RemoveAt(line.Count - 1);
            // Everything it has that the base doesn't, and the base commits those come from. One such commit is where
            // it meets the base (the merge base); with several, git decides which.
            var reach = new HashSet<string>(StringComparer.Ordinal);
            var meets = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(b.Tip);
            while (stack.Count > 0)
            {
                var sha = stack.Pop();
                if (!own.TryGetValue(sha, out var c)) { meets.Add(sha); continue; }
                if (!reach.Add(sha)) continue;
                foreach (var p in c.Parents) stack.Push(p);
            }
            return (b, line, truncated, reach, meets);
        }).ToList();

        var forkShas = branches.Where(x => !x.truncated && x.line.Count > 0 && x.line[^1].Parents.Length > 0).Select(x => x.line[^1].Parents[0]).Distinct().ToList();
        var forksTask = AnchorsAsync(dir, forkShas);
        var mergeBaseTask = Throttled(branches, x => x.meets.Count == 1 ? Task.FromResult<string?>(x.meets.First()) : MergeBaseAsync(dir, baseTip, x.b.Tip));
        var aheadTask = Throttled(branches, x => x.b.Counted ? Task.FromResult((x.b.Ahead, x.b.Behind)) : GitService.AheadBehindAsync(dir, baseTip, x.b.Tip));
        var merges = branches.SelectMany((x, k) => x.line.Select((c, i) => (k, i, c)).Where(m => m.c.Parents.Length >= 2)).ToList();
        var takenTask = Throttled(merges, m => TakenAsync(dir, $"{m.c.Parents[0]}..{m.c.Parents[1]}"));

        var mergeBases = await mergeBaseTask;
        var froms = branches.Select((x, k) => mergeBases[k] ?? (x.line.Count > 0 && x.line[^1].Parents.Length > 0 ? x.line[^1].Parents[0] : x.b.Tip)).ToList();
        var files = await NameStatusManyAsync(dir, branches.Select((x, k) => (froms[k], x.b.Tip)).ToList());
        var forks = await forksTask;
        var aheads = await aheadTask;
        var taken = await takenTask;

        var commits = branches.Select(x => x.line.Select(c => new FocusCommit(c.Sha, c.Author, c.Date, c.Subject, c.Parents, false, 0, null)).ToList()).ToList();
        for (var j = 0; j < merges.Count; j++)
        {
            var (k, i, c) = merges[j];
            var fromBase = !own.ContainsKey(c.Parents[1]);   // the base has it: the branch took the base in
            commits[k][i] = commits[k][i] with { FromBase = fromBase, TookIn = taken[j].Count, Taken = fromBase ? taken[j].First : null };
        }
        return branches.Select((x, k) =>
        {
            var fork = !x.truncated && x.line.Count > 0 && x.line[^1].Parents.Length > 0 ? forks.GetValueOrDefault(x.line[^1].Parents[0]) : null;
            var (ahead, behind) = aheads[k];
            return (x.b.Ref, new BranchFocus(baseName, x.b.Tip, null, fork, commits[k], froms[k], files[k], ahead, behind, x.truncated));
        }).ToList();
    }

    /// <summary>Several commits' subjects and dates, from one call.</summary>
    private static async Task<Dictionary<string, FocusAnchor>> AnchorsAsync(string dir, List<string> shas)
    {
        var map = new Dictionary<string, FocusAnchor>(StringComparer.Ordinal);
        if (shas.Count == 0) return map;
        var r = await GitService.RunAsync(dir, ["log", "--no-walk=unsorted", "--format=%H%x1f%s%x1f%aI%x1e", "--end-of-options", .. shas, "--"]);
        foreach (var rec in r.Ok ? r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length >= 3) map[f[0]] = new(f[0], f[1], f[2]);
        }
        return map;
    }

    /// <summary>
    /// What changed between each pair, from one diff-tree: every pair goes in as a "to from" line, and comes out as
    /// "to" followed by its changes, in the same order (--always: even when nothing changed).
    /// </summary>
    private static async Task<List<List<FileChange>>> NameStatusManyAsync(string dir, List<(string From, string To)> pairs)
    {
        var lists = pairs.Select(_ => new List<FileChange>()).ToList();
        if (pairs.Count == 0) return lists;
        var r = await GitService.RunAsync(dir, ["diff-tree", "--stdin", "--always", "-r", "-z", "--name-status", "-M", "--no-ext-diff", "--no-textconv"],
            stdin: string.Concat(pairs.Select(p => $"{p.To} {p.From}\n")), maxOutputBytes: 32 << 20);
        var parts = r.Ok ? r.Text.Split('\0') : [];
        var k = -1;
        for (var i = 0; i < parts.Length; i++)
        {
            var status = parts[i];
            if (status.Length == 0) continue;
            // A status is a letter and maybe a score (R100); a 40- or 64-character id starts the next pair.
            if (Sha.IsMatch(status)) { k++; continue; }
            if (k < 0 || k >= lists.Count) break;
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= parts.Length) break;
                lists[k].Add(new FileChange(parts[i + 2], parts[i + 1], status[0] == 'R' ? "R" : "A"));
                i += 2;
            }
            else
            {
                if (i + 1 >= parts.Length) break;
                lists[k].Add(new FileChange(parts[i + 1], null, status[0] is 'M' or 'A' or 'D' ? status[..1] : "M"));
                i += 1;
            }
        }
        return lists;
    }

    /// <summary>A branch by name: refs/heads/name, refs/remotes/origin/name, else the newest merge on the base naming it.</summary>
    private static async Task<(string? Ref, string? Merge)> FindByNameAsync(string dir, string name, string? baseTip)
    {
        // All three asked at once: a git call costs tens of milliseconds just to start.
        var local = ResolveAsync(dir, "refs/heads/" + name);
        var remote = ResolveAsync(dir, "refs/remotes/origin/" + name);
        // --fixed-strings: the name is matched as written; "--grep=" keeps it a value, never an option.
        var logTask = baseTip == null ? Task.FromResult<GitResult?>(null)! : GitService.RunAsync(dir, ["log", "--merges", "--first-parent", "-n20",
            "--fixed-strings", "--grep=" + name, "--format=%H%x1f%s%x1e", "--end-of-options", baseTip, "--"]);
        if (await local != null) return ("refs/heads/" + name, null);
        if (await remote != null) return ("refs/remotes/origin/" + name, null);
        if (await logTask is not { } log) return (null, null);
        foreach (var rec in log.Ok ? log.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 2) continue;
            var s = f[1];
            // The name as the merged branch, not merely mentioned: "from owner/name", "branch 'name'".
            if (s.EndsWith("/" + name, StringComparison.Ordinal) || s.Contains("/" + name + " ", StringComparison.Ordinal)
                || s.Contains($"'{name}'", StringComparison.Ordinal) || s.Contains($"'origin/{name}'", StringComparison.Ordinal))
                return (null, f[0]);
        }
        return (null, null);
    }

    /// <summary>What a merge brought in: how many commits, and the first <see cref="MaxTaken"/> of them, from one call.</summary>
    private static async Task<(int Count, List<FocusTaken> First)> TakenAsync(string dir, string range)
    {
        var r = await GitService.RunAsync(dir, ["log", "--format=%H%x1f%s%x1f%an%x1f%aI%x1e", "--end-of-options", range, "--"], maxOutputBytes: 8 << 20);
        var all = (r.Ok ? r.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries) : []).Select(x => x.Trim('\n', '\r').Split('\x1f'))
            .Where(f => f.Length >= 4).ToList();
        return (all.Count, all.Take(MaxTaken).Select(f => new FocusTaken(f[0], f[1], f[2], f[3])).ToList());
    }

    private static async Task<List<FileChange>> NameStatusAsync(string dir, string from, string to)
    {
        var r = await GitService.RunAsync(dir, ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M", "--end-of-options", from, to, "--"],
            maxOutputBytes: 8 << 20);
        var files = new List<FileChange>();
        var parts = r.Ok ? r.Text.Split('\0') : [];
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            var status = parts[i];
            if (status.Length == 0) continue;
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 2], parts[i + 1], status[0] == 'R' ? "R" : "A"));
                i += 2;
            }
            else
            {
                files.Add(new FileChange(parts[i + 1], null, status[0] is 'M' or 'A' or 'D' ? status[..1] : "M"));
                i += 1;
            }
        }
        return files;
    }

    private static async Task<FocusAnchor?> AnchorAsync(string dir, string sha)
    {
        var r = await GitService.RunAsync(dir, ["log", "-1", "--format=%H%x1f%s%x1f%aI", "--end-of-options", sha, "--"]);
        var f = r.Ok ? r.Text.TrimEnd('\n', '\r').Split('\x1f') : [];
        return f.Length >= 3 ? new(f[0], f[1], f[2]) : null;
    }

    private static async Task<IReadOnlyList<string>?> ParentsAsync(string dir, string sha)
    {
        var r = await GitService.RunAsync(dir, ["log", "-1", "--format=%P", "--end-of-options", sha, "--"]);
        return r.Ok ? r.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    private static async Task<string?> ResolveAsync(string dir, string rev)
    {
        var r = await GitService.RunAsync(dir, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"]);
        return r.Ok ? r.Text.Trim() : null;
    }

    private static async Task<string?> MergeBaseAsync(string dir, string a, string b)
    {
        var r = await GitService.RunAsync(dir, ["merge-base", "--end-of-options", a, b]);
        return r.Ok ? r.Text.Trim() : null;
    }

    private static async Task<bool> IsAncestorAsync(string dir, string a, string b) =>
        (await GitService.RunAsync(dir, ["merge-base", "--is-ancestor", "--end-of-options", a, b])).Ok;

    private static async Task<T[]> Throttled<TIn, T>(IEnumerable<TIn> items, Func<TIn, Task<T>> work)
    {
        using var gate = new SemaphoreSlim(6);
        return await Task.WhenAll(items.Select(async x =>
        {
            await gate.WaitAsync();
            try { return await work(x); }
            finally { gate.Release(); }
        }));
    }
}


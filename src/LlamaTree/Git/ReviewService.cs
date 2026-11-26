using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaTree.Sessions;

namespace LlamaTree.Git;

/// <summary>One file a branch changes, as reviewed.</summary>
/// <param name="Blob">The file's content id at the tip (its old one when deleted): a file marked viewed stays viewed only
/// while this is unchanged.</param>
internal sealed record ReviewFile(string Path, string? OrigPath, string Status, int Added, int Removed, bool Binary, string Blob);

/// <summary>A review comment, on a line of a file as it was at <paramref name="AtTip"/> (line 0: the whole file).</summary>
/// <param name="State">"pending" (not sent yet), "sent" (sent to an agent) or "resolved".</param>
internal sealed record ReviewComment(string Id, string Path, int Line, string LineText, string Text, string AtTip, string State,
    DateTimeOffset Created, DateTimeOffset? SentAt);

/// <summary>A comment where it is now: <paramref name="Line"/> in the file at the branch's tip.</summary>
/// <param name="Changed">The lines around it changed since it was written or sent (for a sent comment, perhaps addressed).</param>
/// <param name="Gone">The file is no longer changed by the branch, or no longer there.</param>
internal sealed record ReviewCommentView(ReviewComment Comment, int Line, bool Changed, bool Gone);

/// <summary>What changed since the last review: only the branch's own files, so what came in from the base is left out.</summary>
/// <param name="Commits">Commits since then that the base doesn't have.</param>
/// <param name="BaseCommits">Commits since then that came in from the base (merged in).</param>
internal sealed record ReviewSince(string Tip, DateTimeOffset At, int Commits, int BaseCommits, List<ReviewFile> Files);

/// <param name="From">Where the branch's changes are measured from: where it last met the base.</param>
/// <param name="Viewed">Files marked viewed, by path, with the content id they had then.</param>
/// <param name="Approved">Approved at this tip (nothing new since).</param>
internal sealed record ReviewView(string Repo, string Branch, string? BaseBranch, string Tip, string From, int Ahead, int Behind,
    List<ReviewFile> Files, Dictionary<string, string> Viewed, ReviewSince? Since, List<ReviewCommentView> Comments,
    string? ApprovedTip, string? ReviewedTip);

/// <summary>A branch's review in a word, for In flight, Git Changes and the list of reviews.</summary>
/// <param name="Tip">The branch's tip now (its local copy, else its remote one); null when it has gone.</param>
/// <param name="Ref">The branch's ref, to open its review by: refs/heads/x, else refs/remotes/origin/x.</param>
/// <param name="Merged">The branch is in the base branch now: its review is done.</param>
/// <param name="Sent">Comments sent and not resolved; <paramref name="Pending"/>: written and not sent yet.</param>
/// <param name="Updated">The last thing done in it: started, a comment written or sent, reviewed.</param>
internal sealed record ReviewSummary(string? Tip, string? Ref, bool Merged, string? ReviewedTip, DateTimeOffset? ReviewedAt, string? ApprovedTip,
    int Pending, int Open, int Sent, int Viewed, DateTimeOffset? Started, DateTimeOffset? Updated);

/// <summary>A branch with work of its own the base doesn't have, and no review yet.</summary>
internal sealed record ReviewCandidate(string Name, string Ref, string Date, string Subject);

/// <summary>One repository's reviews, for the Reviews tab: by branch name, and the branches that could be reviewed.</summary>
/// <param name="Repo">The repository, by its main worktree: the same from any of its worktrees.</param>
internal sealed record ReviewList(string Repo, string? BaseBranch, Dictionary<string, ReviewSummary> Reviews, List<ReviewCandidate> Unreviewed);

/// <summary>What's kept of one branch's review.</summary>
internal sealed class ReviewState
{
    public string? ReviewedTip { get; set; }
    public string? ReviewedFrom { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ApprovedTip { get; set; }
    /// <summary>When the review was first opened: from then on it's in the list of reviews, until it's forgotten.</summary>
    public DateTimeOffset? Started { get; set; }
    public Dictionary<string, string> Viewed { get; set; } = [];
    public List<ReviewComment> Comments { get; set; } = [];
}

/// <summary>
/// Reviews, kept in reviews.json under the app data folder: by repository (its main worktree) and branch name, so a
/// branch's review is the same from every worktree and from its remote copy.
/// </summary>
internal sealed class ReviewStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? path;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, ReviewState>> repos;

    /// <param name="path">Where it's kept; null keeps it in memory only (tests).</param>
    public ReviewStore(string? path)
    {
        this.path = path;
        repos = Load(path);
    }

    /// <summary>A copy of the branch's review (a fresh one when there's none yet).</summary>
    public ReviewState Get(string repo, string branch)
    {
        lock (gate) return Copy(repos.GetValueOrDefault(RepoTrustStore.Key(repo))?.GetValueOrDefault(branch));
    }

    public Dictionary<string, ReviewState> All(string repo)
    {
        lock (gate) return (repos.GetValueOrDefault(RepoTrustStore.Key(repo)) ?? []).ToDictionary(x => x.Key, x => Copy(x.Value));
    }

    /// <summary>Changes the branch's review and keeps it.</summary>
    public T Update<T>(string repo, string branch, Func<ReviewState, T> change)
    {
        lock (gate)
        {
            var key = RepoTrustStore.Key(repo);
            if (!repos.TryGetValue(key, out var branches)) repos[key] = branches = [];
            if (!branches.TryGetValue(branch, out var st)) branches[branch] = st = new ReviewState();
            var result = change(st);
            Save();
            return result;
        }
    }

    /// <summary>Forgets the branch's review altogether.</summary>
    public bool Remove(string repo, string branch)
    {
        lock (gate)
        {
            if (!repos.TryGetValue(RepoTrustStore.Key(repo), out var branches) || !branches.Remove(branch)) return false;
            Save();
            return true;
        }
    }

    private static ReviewState Copy(ReviewState? s) => s == null ? new() : new()
    {
        ReviewedTip = s.ReviewedTip, ReviewedFrom = s.ReviewedFrom, ReviewedAt = s.ReviewedAt, ApprovedTip = s.ApprovedTip, Started = s.Started,
        Viewed = new(s.Viewed), Comments = [.. s.Comments],
    };

    private static Dictionary<string, Dictionary<string, ReviewState>> Load(string? path)
    {
        try
        {
            if (path != null && File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ReviewState>>>(File.ReadAllText(path), Options) is { } read)
                return read;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file starts the reviews over; nothing else depends on them.
        }
        return [];
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(repos, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written with the next change.
        }
    }
}

/// <summary>
/// Reviewing a branch in LlamaTree: the files it changes against the base, which of them you've viewed (until they
/// change again), comments on their lines that can be sent to the agent working on it, and what changed since you last
/// reviewed it. Reads git only; the review itself is kept in <see cref="Store"/>.
/// </summary>
internal static class ReviewService
{
    public const int MaxCommentLength = 4000;
    public const int MaxComments = 200;

    private static readonly Regex Sha = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);
    private static readonly Regex Hunk = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static ReviewStore? store;

    /// <summary>Where reviews are kept (reviews.json in the app data folder); tests put an in-memory one here.</summary>
    internal static ReviewStore Store
    {
        get => LazyInitializer.EnsureInitialized(ref store, () => new ReviewStore(Path.Combine(SessionStore.AppDataFolder, "reviews.json")));
        set => store = value;
    }

    /// <summary>The branch's name for its review: the same for refs/heads/x and refs/remotes/origin/x.</summary>
    internal static string? BranchOf(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..]
        : refName.StartsWith("refs/remotes/", StringComparison.Ordinal) && refName.IndexOf('/', "refs/remotes/".Length) is > 0 and var i ? refName[(i + 1)..]
        : null;

    private sealed record Where(string Repo, string Branch, string Tip, string? BaseName, string? BaseTip, string From);

    private static async Task<Where> LocateAsync(string dir, string refName)
    {
        var branch = BranchOf(refName) ?? throw new InvalidOperationException("Only a branch can be reviewed.");
        var tipTask = ResolveAsync(dir, refName);
        var worktreesTask = GitService.GetWorktreesAsync(dir);
        var baseName = await GitService.GetBaseBranchAsync(dir);
        var baseTip = baseName == null ? null : await ResolveAsync(dir, baseName);
        var tip = await tipTask ?? throw new InvalidOperationException($"There's no branch {branch} any more.");
        var worktrees = await worktreesTask;
        var repo = worktrees.Count > 0 ? worktrees[0].Path : dir;
        var from = baseTip == null ? tip : await MergeBaseAsync(dir, baseTip, tip) ?? baseTip;
        return new(repo, branch, tip, baseName, baseTip, from);
    }

    /// <summary>The branch's review as it stands: its files, what you've viewed and written, and what changed since.</summary>
    public static async Task<ReviewView> GetAsync(string dir, string refName)
    {
        var w = await LocateAsync(dir, refName);
        var st = Store.Get(w.Repo, w.Branch);
        var filesTask = FilesAsync(dir, w.From, w.Tip);
        var aheadTask = w.BaseTip == null ? Task.FromResult((0, 0)) : GitService.AheadBehindAsync(dir, w.BaseTip, w.Tip);
        var files = await filesTask;

        ReviewSince? since = null;
        if (st.ReviewedTip is { } was && Sha.IsMatch(was) && was != w.Tip && await ResolveAsync(dir, was) != null)
        {
            // Only the branch's own files: those it changes now, or did when you reviewed it. What came in from the base
            // alone (a merge from main) isn't the branch's work, so it's left out.
            var ownNow = files.SelectMany(f => new[] { f.Path, f.OrigPath }).OfType<string>();
            var ownThen = st.ReviewedFrom is { } rf && Sha.IsMatch(rf) ? await NamesAsync(dir, rf, was) : [];
            var own = ownNow.Concat(ownThen).ToHashSet(StringComparer.Ordinal);
            var changedTask = FilesAsync(dir, was, w.Tip);
            var allTask = CountAsync(dir, [$"{was}..{w.Tip}"]);
            var ownCommitsTask = w.BaseTip == null ? allTask : CountAsync(dir, [$"{was}..{w.Tip}", "^" + w.BaseTip]);
            var changed = (await changedTask).Where(f => own.Contains(f.Path) || f.OrigPath != null && own.Contains(f.OrigPath)).ToList();
            var all = await allTask;
            var ownCommits = await ownCommitsTask;
            since = new(was, st.ReviewedAt ?? DateTimeOffset.MinValue, ownCommits, Math.Max(0, all - ownCommits), changed);
        }

        var inBranch = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var comments = await Task.WhenAll(st.Comments.Select(async c =>
        {
            var gone = !inBranch.Contains(c.Path);
            if (c.AtTip == w.Tip) return new ReviewCommentView(c, c.Line, false, gone);
            var (line, changed) = await MapLineAsync(dir, c.AtTip, w.Tip, c.Path, c.Line);
            return new ReviewCommentView(c, line, changed, gone);
        }));
        var (ahead, behind) = await aheadTask;
        return new(w.Repo, w.Branch, w.BaseName, w.Tip, w.From, ahead, behind, files, st.Viewed, since, [.. comments],
            st.ApprovedTip, st.ReviewedTip);
    }

    /// <summary>Each branch's review in a word, for In flight.</summary>
    public static async Task<Dictionary<string, ReviewSummary>> SummariesAsync(string dir)
    {
        var worktreesTask = GitService.GetWorktreesAsync(dir);
        var baseName = await GitService.GetBaseBranchAsync(dir);
        // Every branch's tip from one call, a local branch over its remote copy, and whether the base has it already.
        var refsTask = GitService.RunAsync(dir, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes"]);
        var mergedTask = baseName == null ? Task.FromResult<GitResult?>(null)!
            : GitService.RunAsync(dir, ["for-each-ref", "--format=%(refname)", $"--merged=refs/{(baseName.Contains('/') ? "remotes" : "heads")}/{baseName}", "refs/heads", "refs/remotes"]);
        var refs = await refsTask;
        var tips = new Dictionary<string, (string Sha, string Ref)>(StringComparer.Ordinal);
        foreach (var line in refs.Ok ? refs.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var sp = line.IndexOf(' ');
            var full = sp < 0 ? "" : line[(sp + 1)..].Trim();
            if (BranchOf(full) is not { } name || full.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            if (full.StartsWith("refs/heads/", StringComparison.Ordinal) || !tips.ContainsKey(name)) tips[name] = (line[..sp], full);
        }
        var merged = (await mergedTask) is { Ok: true } m ? m.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal) : [];
        var worktrees = await worktreesTask;
        var repo = worktrees.Count > 0 ? worktrees[0].Path : dir;
        return Store.All(repo).ToDictionary(x => x.Key, x =>
        {
            var st = x.Value;
            var tip = tips.TryGetValue(x.Key, out var t) ? t : default;
            var updated = new[] { st.Started, st.ReviewedAt }.Concat(st.Comments.Select(c => (DateTimeOffset?)(c.SentAt ?? c.Created))).Max();
            return new ReviewSummary(tip.Sha, tip.Ref, tip.Ref != null && merged.Contains(tip.Ref), st.ReviewedTip, st.ReviewedAt, st.ApprovedTip,
                st.Comments.Count(c => c.State == "pending"), st.Comments.Count(c => c.State != "resolved"), st.Comments.Count(c => c.State == "sent"),
                st.Viewed.Count, st.Started, updated);
        });
    }

    public const int MaxCandidates = 30;

    /// <summary>
    /// The repository's reviews, and the local branches with work the base doesn't have and no review yet (newest first),
    /// for the Reviews tab.
    /// </summary>
    public static async Task<ReviewList> ListAsync(string dir)
    {
        var reviewsTask = SummariesAsync(dir);
        var worktreesTask = GitService.GetWorktreesAsync(dir);
        var baseName = await GitService.GetBaseBranchAsync(dir);
        var baseRef = baseName == null ? null : $"refs/{(baseName.Contains('/') ? "remotes" : "heads")}/{baseName}";
        var open = baseRef == null ? null : await GitService.RunAsync(dir, ["for-each-ref", "--sort=-committerdate", $"--no-merged={baseRef}",
            "--format=%(refname)%1f%(committerdate:iso-strict)%1f%(contents:subject)", "refs/heads"]);
        var reviews = await reviewsTask;
        var worktrees = await worktreesTask;
        var candidates = new List<ReviewCandidate>();
        foreach (var line in open is { Ok: true } ? open.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            var f = line.Trim().Split('\x1f');
            if (f.Length < 3 || BranchOf(f[0]) is not { } name || reviews.ContainsKey(name)) continue;
            candidates.Add(new(name, f[0], f[1], f[2]));
            if (candidates.Count >= MaxCandidates) break;
        }
        return new(worktrees.Count > 0 ? worktrees[0].Path : dir, baseName, reviews, candidates);
    }

    /// <summary>The review was opened: it's in the list of reviews from now on.</summary>
    public static async Task StartAsync(string dir, string refName)
    {
        var w = await LocateAsync(dir, refName);
        Store.Update(w.Repo, w.Branch, st => st.Started ??= DateTimeOffset.UtcNow);
    }

    /// <summary>Forgets a branch's review: its ticks, comments and approval. By name, so a branch since deleted can go too.</summary>
    public static async Task ForgetAsync(string dir, string branch)
    {
        var worktrees = await GitService.GetWorktreesAsync(dir);
        Store.Remove(worktrees.Count > 0 ? worktrees[0].Path : dir, branch);
    }

    public static async Task SetViewedAsync(string dir, string refName, string path, string? blob)
    {
        var w = await LocateAsync(dir, refName);
        Store.Update(w.Repo, w.Branch, st =>
        {
            if (blob == null) st.Viewed.Remove(path);
            else st.Viewed[path] = blob;
            return true;
        });
    }

    /// <summary>A new comment, pending until sent, on a line of the file as it is at the tip (line 0: the whole file).</summary>
    public static async Task<ReviewComment> AddCommentAsync(string dir, string refName, string path, int line, string? lineText, string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Write the comment first.");
        if (text.Length > MaxCommentLength) throw new InvalidOperationException($"A comment can be up to {MaxCommentLength} characters.");
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || line < 0) throw new InvalidOperationException("Not a line of a file.");
        var w = await LocateAsync(dir, refName);
        var quoted = (lineText ?? "").Trim();
        if (quoted.Length > 300) quoted = quoted[..300];
        var c = new ReviewComment(Guid.NewGuid().ToString("N")[..12], path, line, quoted, text, w.Tip, "pending", DateTimeOffset.UtcNow, null);
        return Store.Update(w.Repo, w.Branch, st =>
        {
            if (st.Comments.Count >= MaxComments) throw new InvalidOperationException($"A review holds up to {MaxComments} comments: resolve some first.");
            st.Comments.Add(c);
            return c;
        });
    }

    public static async Task EditCommentAsync(string dir, string refName, string id, string? text, string? state)
    {
        if (state is not (null or "pending" or "sent" or "resolved")) throw new InvalidOperationException("Not a comment state.");
        text = text?.Trim();
        if (text is { Length: 0 }) throw new InvalidOperationException("Write the comment first.");
        if (text?.Length > MaxCommentLength) throw new InvalidOperationException($"A comment can be up to {MaxCommentLength} characters.");
        var w = await LocateAsync(dir, refName);
        Store.Update(w.Repo, w.Branch, st =>
        {
            var i = st.Comments.FindIndex(c => c.Id == id);
            if (i < 0) throw new InvalidOperationException("That comment has gone.");
            st.Comments[i] = st.Comments[i] with { Text = text ?? st.Comments[i].Text, State = state ?? st.Comments[i].State };
            return true;
        });
    }

    public static async Task DeleteCommentAsync(string dir, string refName, string id)
    {
        var w = await LocateAsync(dir, refName);
        Store.Update(w.Repo, w.Branch, st => st.Comments.RemoveAll(c => c.Id == id));
    }

    /// <summary>
    /// The review was sent (to an agent) or approved, or simply marked reviewed: what changes after this is "since your
    /// review". Sending moves the comments sent to where their lines are at this tip, so what changes around them from
    /// here on shows.
    /// </summary>
    /// <param name="sent">The comments that were sent.</param>
    public static async Task MarkReviewedAsync(string dir, string refName, IReadOnlyCollection<string> sent, bool approve)
    {
        var w = await LocateAsync(dir, refName);
        var st = Store.Get(w.Repo, w.Branch);
        var moved = await Task.WhenAll(st.Comments.Where(c => sent.Contains(c.Id) && c.AtTip != w.Tip).Select(async c =>
            (c.Id, (await MapLineAsync(dir, c.AtTip, w.Tip, c.Path, c.Line)).Line)));
        var lines = moved.ToDictionary(x => x.Id, x => x.Line);
        Store.Update(w.Repo, w.Branch, s =>
        {
            for (var i = 0; i < s.Comments.Count; i++)
            {
                var c = s.Comments[i];
                if (!sent.Contains(c.Id)) continue;
                s.Comments[i] = c with { State = "sent", SentAt = DateTimeOffset.UtcNow, AtTip = w.Tip, Line = lines.GetValueOrDefault(c.Id, c.Line) };
            }
            s.ReviewedTip = w.Tip;
            s.ReviewedFrom = w.From;
            s.ReviewedAt = DateTimeOffset.UtcNow;
            if (approve) s.ApprovedTip = w.Tip;
            return true;
        });
    }

    /// <summary>
    /// Where line <paramref name="line"/> of <paramref name="path"/> at <paramref name="from"/> is at <paramref name="to"/>,
    /// and whether the lines around it changed in between (line 0, the whole file: whether the file changed at all).
    /// </summary>
    internal static async Task<(int Line, bool Changed)> MapLineAsync(string dir, string from, string to, string path, int line)
    {
        if (!Sha.IsMatch(from)) return (line, false);
        var r = await GitService.RunAsync(dir, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "-U0", "--end-of-options", from, to, "--", path],
            maxOutputBytes: 8 << 20);
        if (!r.Ok) return (line, false);
        var hunks = Hunk.Matches(r.Text);
        if (line == 0) return (0, hunks.Count > 0);
        var delta = 0;
        foreach (Match m in hunks)
        {
            var oldStart = int.Parse(m.Groups[1].Value);
            var oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            var newStart = int.Parse(m.Groups[3].Value);
            var newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
            // An insertion (no old lines) goes after oldStart; a change covers oldStart.. for oldCount lines.
            var first = oldCount == 0 ? oldStart + 1 : oldStart;
            var last = oldCount == 0 ? oldStart : oldStart + oldCount - 1;
            // Changed: the hunk touches the line or the two either side of it.
            if (first <= line + 2 && last >= line - 2)
            {
                var at = line >= first && line <= last ? newStart + Math.Min(line - first, Math.Max(0, newCount - 1)) : line + delta;
                return (Math.Max(1, at), true);
            }
            if (last < line) delta += newCount - oldCount;
            else break;
        }
        return (Math.Max(1, line + delta), false);
    }

    /// <summary>What changed between two commits, file by file: status, lines added and removed, and content ids.</summary>
    internal static async Task<List<ReviewFile>> FilesAsync(string dir, string from, string to)
    {
        if (from == to) return [];
        string[] common = ["--no-ext-diff", "--no-textconv", "-M", "-z", "--end-of-options", from, to, "--"];
        var rawTask = GitService.RunAsync(dir, ["diff", "--raw", "--abbrev=40", .. common], maxOutputBytes: 16 << 20);
        var numTask = GitService.RunAsync(dir, ["diff", "--numstat", .. common], maxOutputBytes: 16 << 20);
        var raw = await rawTask;
        var num = await numTask;

        var counts = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        var np = num.Ok ? num.Text.Split('\0') : [];
        for (var i = 0; i < np.Length; i++)
        {
            var f = np[i].Split('\t');
            if (f.Length < 3) continue;
            string path;
            if (f[2].Length == 0) { if (i + 2 >= np.Length) break; path = np[i + 2]; i += 2; }   // a rename: old and new follow
            else path = f[2];
            var binary = f[0] == "-";
            counts[path] = (binary ? 0 : int.TryParse(f[0], out var a) ? a : 0, binary ? 0 : int.TryParse(f[1], out var d) ? d : 0, binary);
        }

        var files = new List<ReviewFile>();
        var rp = raw.Ok ? raw.Text.Split('\0') : [];
        for (var i = 0; i < rp.Length; i++)
        {
            if (!rp[i].StartsWith(':')) continue;
            var meta = rp[i].Split(' ');
            if (meta.Length < 5 || i + 1 >= rp.Length) break;
            var status = meta[4][..1];
            string? orig = null;
            var path = rp[i + 1];
            if (status is "R" or "C")
            {
                if (i + 2 >= rp.Length) break;
                orig = rp[i + 1];
                path = rp[i + 2];
                i += 2;
            }
            else i += 1;
            var blob = status == "D" ? "deleted:" + meta[2] : meta[3];
            var (added, removed, binary) = counts.GetValueOrDefault(path);
            files.Add(new(path, status == "C" ? null : orig, status is "A" or "D" or "R" ? status : status == "C" ? "A" : "M", added, removed, binary, blob));
        }
        return files;
    }

    private static async Task<List<string>> NamesAsync(string dir, string from, string to)
    {
        var r = await GitService.RunAsync(dir, ["diff", "--name-only", "-z", "--no-ext-diff", "--end-of-options", from, to, "--"], maxOutputBytes: 16 << 20);
        return r.Ok ? [.. r.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : [];
    }

    private static async Task<int> CountAsync(string dir, IEnumerable<string> revs)
    {
        var r = await GitService.RunAsync(dir, ["rev-list", "--count", "--end-of-options", .. revs, "--"]);
        return r.Ok && int.TryParse(r.Text.Trim(), out var n) ? n : 0;
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
}

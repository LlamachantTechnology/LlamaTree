using LlamaTree.Git;
using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>
/// Git Repository's In flight view: the branches not on main yet, their uncommitted work, and where they'd collide with
/// main and with each other. In the non-parallel collection because one test changes the trust store for the process.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class InFlightTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string worktree = Path.Combine(Path.GetTempPath(), "ct-inflight-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepoTrustStore previousStore = RepoTrust.Store;
    private readonly string start;

    public InFlightTests()
    {
        RepoTrust.Store = new RepoTrustStore(null);
        RepoTrust.ClearCache();
        repo.Commit(("app.js", "one\ntwo\nthree\n"), ("docs.md", "docs\n"), ("other.txt", "other\n"));
        start = Sha("main");

        Branch("conflicting-a", ("app.js", "one\nA\nthree\n"));
        Branch("conflicting-b", ("app.js", "one\nB\nthree\n"));
        Branch("docs-only", ("docs.md", "better docs\n"));
        Branch("merged", ("other.txt", "merged\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "merge merged", "merged");
        repo.Commit(("other.txt", "main moved on\n"));

        // A worktree with uncommitted changes to the file docs-only changes, and no commits of its own.
        repo.Git("worktree", "add", "-q", "-b", "agent-wip", worktree, "main");
        File.WriteAllText(Path.Combine(worktree, "docs.md"), "half done\n");
    }

    public void Dispose()
    {
        try { repo.Git("worktree", "remove", "--force", worktree); } catch (InvalidOperationException) { }
        repo.Dispose();
        RepoTrust.Store = previousStore;
        RepoTrust.ClearCache();
    }

    private string Sha(string rev) => repo.Git("rev-parse", rev).Trim();

    private void Branch(string name, params (string Path, string Content)[] files)
    {
        repo.Git("checkout", "-q", "-b", name, "main");
        repo.Commit(files);
        repo.Git("checkout", "-q", "main");
    }

    [Fact]
    public async Task Lists_branches_with_work_main_lacks_and_leaves_out_merged_ones()
    {
        var report = await InFlightService.GetAsync(repo.Root);

        Assert.Equal("main", report.BaseBranch);
        Assert.Equal(["agent-wip", "conflicting-a", "conflicting-b", "docs-only"], report.Branches.Select(b => b.Name).Order());
        var wip = report.Branches.Single(b => b.Name == "agent-wip");
        Assert.Equal((0, 1), (wip.Ahead, wip.Uncommitted));
        Assert.True(PathText.SamePlace(worktree, wip.Worktree!));
        var a = report.Branches.Single(b => b.Name == "conflicting-a");
        Assert.Equal((1, 3, 1), (a.Ahead, a.Behind, a.Files));   // behind: the merged branch's commit, its merge, and one more
    }

    [Fact]
    public async Task Each_branch_is_checked_against_main()
    {
        repo.Commit(("app.js", "one\nMAIN\nthree\n"));   // now conflicting-a and -b both conflict with main

        var report = await InFlightService.GetAsync(repo.Root);

        Assert.Equal("on", report.ConflictCheck);
        Assert.Equal(["app.js"], report.Branches.Single(b => b.Name == "conflicting-a").BaseConflicts);
        Assert.Empty(report.Branches.Single(b => b.Name == "docs-only").BaseConflicts!);
        Assert.Null(report.Branches.Single(b => b.Name == "agent-wip").BaseConflicts);   // nothing committed to merge
    }

    [Fact]
    public async Task Branches_changing_the_same_files_are_tried_against_each_other()
    {
        var report = await InFlightService.GetAsync(repo.Root);

        var clash = Assert.Single(report.Overlaps, o => o.A.StartsWith("conflicting") && o.B.StartsWith("conflicting"));
        Assert.Equal(["app.js"], clash.Files);
        Assert.Equal(["app.js"], clash.Conflicts);
        // Uncommitted work overlaps too, but there's nothing committed to merge on one side.
        var wip = Assert.Single(report.Overlaps, o => new[] { o.A, o.B }.Order().SequenceEqual(["agent-wip", "docs-only"]));
        Assert.Equal(["docs.md"], wip.Files);
        Assert.Null(wip.Conflicts);
        Assert.Equal(2, report.Overlaps.Count);   // docs-only and conflicting-* share nothing
    }

    [Fact]
    public async Task Leaves_every_branch_and_worktree_as_it_was()
    {
        var before = repo.Snapshot();
        var refs = repo.Git("for-each-ref");

        await InFlightService.GetAsync(repo.Root);

        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(refs, repo.Git("for-each-ref"));
        Assert.Equal("half done\n", File.ReadAllText(Path.Combine(worktree, "docs.md")));
    }

    [Fact]
    public async Task Lists_what_landed_on_main_since_a_commit()
    {
        var report = await InFlightService.GetAsync(repo.Root, since: start);

        Assert.Equal(["commit", "merge merged"], report.Landed!.Select(c => c.Subject));   // first-parent: the merge, not its branch
        Assert.Empty((await InFlightService.GetAsync(repo.Root, since: Sha("main"))).Landed!);
        Assert.Null((await InFlightService.GetAsync(repo.Root, since: Sha("conflicting-a"))).Landed);   // not behind main
        Assert.Null((await InFlightService.GetAsync(repo.Root, since: "--all")).Landed);   // not a commit id: never passed to git
    }

    /// <summary>A bare repository as origin, in a folder of its own.</summary>
    private string AddOrigin()
    {
        var bare = Path.Combine(Path.GetTempPath(), "ct-origin-" + Guid.NewGuid().ToString("N")[..8]);
        repo.Git("init", "-q", "--bare", bare);
        repo.Git("remote", "add", "origin", bare);
        return bare;
    }

    [Fact]
    public async Task A_branch_only_on_the_remote_is_listed_as_such()
    {
        var bare = AddOrigin();
        try
        {
            repo.Git("push", "-q", "origin", "main", "docs-only");
            repo.Git("fetch", "-q", "origin");
            repo.Git("branch", "-q", "-D", "docs-only");   // a teammate's branch: on origin, not here

            var report = await InFlightService.GetAsync(repo.Root);

            var remote = Assert.Single(report.Branches, b => b.Remote);
            Assert.Equal(("origin/docs-only", 1), (remote.Name, remote.Ahead));
            Assert.DoesNotContain(report.Branches, b => b.Name == "origin/main");   // the base's own remote copy isn't "in flight"
        }
        finally { FileSystemFixtures.DeleteTree(bare); }
    }

    [Fact]
    public async Task Push_publishes_a_branch_that_isnt_checked_out_here_and_tracks_it()
    {
        var bare = AddOrigin();
        try
        {
            var result = await GitService.PushBranchAsync(repo.Root, "docs-only");

            Assert.True(result.Ok, result.Error);
            Assert.Equal(Sha("docs-only"), repo.Git("--git-dir", bare, "rev-parse", "refs/heads/docs-only").Trim());
            Assert.Equal("origin", repo.Git("config", "--get", "branch.docs-only.remote").Trim());
            Assert.False((await GitService.PushBranchAsync(repo.Root, "--all")).Ok);   // not a branch: never given to git
            Assert.False((await GitService.PushBranchAsync(repo.Root, "no-such-branch")).Ok);
        }
        finally { FileSystemFixtures.DeleteTree(bare); }
    }

    [Fact]
    public async Task Since_you_last_looked_is_kept_per_repository_the_same_from_every_worktree()
    {
        var fromWorktree = await InFlightService.GetAsync(worktree);
        Assert.True(PathText.SamePlace(repo.Root, fromWorktree.Repo));   // the main worktree, asked from another

        var seen = new Dictionary<string, string> { [@"C:\elsewhere"] = Sha("main"), [fromWorktree.Repo] = start };
        var report = await InFlightService.GetAsync(worktree, seen: seen);
        Assert.Equal(2, report.Landed!.Count);
    }

    [Fact]
    public async Task An_untrusted_repository_with_a_merge_driver_gets_no_conflict_checks()
    {
        repo.Git("config", "merge.custom.driver", "some-program %O %A %B");

        var report = await InFlightService.GetAsync(repo.Root);

        Assert.Equal("untrusted", report.ConflictCheck);
        Assert.All(report.Branches, b => Assert.Null(b.BaseConflicts));
        Assert.All(report.Overlaps, o => Assert.Null(o.Conflicts));
        Assert.NotEmpty(report.Overlaps);   // the overlaps themselves still show
    }
}

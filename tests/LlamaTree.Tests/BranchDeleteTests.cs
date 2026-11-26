using LlamaTree.Git;
using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>
/// Delete branch: any local branch, merged or not, with what would be lost; its copy on the remote when asked; and a
/// branch that's only on the remote. In the non-parallel collection because these change the trust store for the process.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class BranchDeleteTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string worktree = Path.Combine(Path.GetTempPath(), "ct-delete-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepoTrustStore previousStore = RepoTrust.Store;
    private string? bare;

    public BranchDeleteTests()
    {
        RepoTrust.Store = new RepoTrustStore(null);
        RepoTrust.ClearCache();
        repo.Commit(("app.js", "one\n"));
        Branch("merged", ("merged.txt", "merged\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "merge merged", "merged");
        Branch("unmerged", ("a.txt", "a\n"), ("b.txt", "b\n"));
    }

    public void Dispose()
    {
        try { repo.Git("worktree", "remove", "--force", worktree); } catch (InvalidOperationException) { }
        repo.Dispose();
        if (bare != null) FileSystemFixtures.DeleteTree(bare);
        RepoTrust.Store = previousStore;
        RepoTrust.ClearCache();
    }

    /// <summary>A branch off main with one commit per file given.</summary>
    private void Branch(string name, params (string Path, string Content)[] files)
    {
        repo.Git("checkout", "-q", "-b", name, "main");
        foreach (var f in files) repo.Commit(f);
        repo.Git("checkout", "-q", "main");
    }

    private bool HasRef(string fullName)
    {
        try { repo.Git("rev-parse", "--verify", "--quiet", fullName); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private bool OnOrigin(string branch)
    {
        try { repo.Git("--git-dir", bare!, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>A bare repository as origin, with main and the given branches pushed and tracked.</summary>
    private void AddOrigin(params string[] branches)
    {
        bare = Path.Combine(Path.GetTempPath(), "ct-origin-" + Guid.NewGuid().ToString("N")[..8]);
        repo.Git("init", "-q", "--bare", bare);
        repo.Git("remote", "add", "origin", bare);
        repo.Git(["push", "-q", "-u", "origin", "main", .. branches]);
    }

    [Fact]
    public async Task A_merged_branch_loses_nothing_and_is_deleted()
    {
        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "merged");
        Assert.True(info.Exists);
        Assert.Equal(("main", true, false, 0), (info.BaseBranch, info.Merged, info.IsBase, info.Lost));
        Assert.Null(info.Worktree);
        Assert.Null(info.Remote);

        var result = await GitService.DeleteBranchAsync(repo.Root, "merged", deleteRemote: true, info.Tip);

        Assert.True(result.Ok, result.Message);
        Assert.False(HasRef("refs/heads/merged"));
    }

    [Fact]
    public async Task An_unmerged_branch_says_how_many_commits_would_go_and_is_still_deleted()
    {
        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "unmerged");
        Assert.Equal((false, 2), (info.Merged, info.Lost));

        var result = await GitService.DeleteBranchAsync(repo.Root, "unmerged", deleteRemote: false, info.Tip);

        Assert.True(result.Ok, result.Message);
        Assert.False(HasRef("refs/heads/unmerged"));
    }

    [Fact]
    public async Task Commits_already_on_the_remote_copy_are_not_counted_as_lost()
    {
        AddOrigin("unmerged");
        repo.Git("checkout", "-q", "unmerged");
        repo.Commit(("c.txt", "c\n"));   // one commit not pushed
        repo.Git("checkout", "-q", "main");

        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "unmerged");

        Assert.Equal((1, "origin", "unmerged"), (info.Lost, info.Remote, info.RemoteBranch));
    }

    [Fact]
    public async Task A_branch_that_moved_since_the_dialog_is_not_deleted()
    {
        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "unmerged");
        repo.Git("checkout", "-q", "unmerged");
        repo.Commit(("late.txt", "late\n"));
        repo.Git("checkout", "-q", "main");

        var result = await GitService.DeleteBranchAsync(repo.Root, "unmerged", deleteRemote: false, info.Tip);

        Assert.False(result.Ok);
        Assert.True(HasRef("refs/heads/unmerged"));
    }

    [Fact]
    public async Task A_branch_checked_out_in_a_worktree_is_refused_naming_the_worktree()
    {
        repo.Git("worktree", "add", "-q", worktree, "unmerged");

        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "unmerged");
        Assert.True(PathText.SamePlace(worktree, info.Worktree!));

        var result = await GitService.DeleteBranchAsync(repo.Root, "unmerged", deleteRemote: false);

        Assert.False(result.Ok);
        Assert.Contains("Switch that worktree", result.Message);
        Assert.True(HasRef("refs/heads/unmerged"));
        Assert.False((await GitService.DeleteBranchAsync(repo.Root, "main", deleteRemote: false)).Ok);   // checked out here
    }

    [Fact]
    public async Task The_base_branch_counts_what_no_other_branch_has()
    {
        repo.Commit(("main-only.txt", "main\n"));
        repo.Git("branch", "keep", "main");
        repo.Git("checkout", "-q", "unmerged");   // main is free to delete now

        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "main");
        Assert.Equal((true, 0), (info.IsBase, info.Lost));   // all on keep

        repo.Git("branch", "-q", "-D", "keep");
        Assert.Equal(1, (await GitService.BranchDeleteInfoAsync(repo.Root, "main")).Lost);   // main-only is only on main
    }

    [Fact]
    public async Task Deletes_the_remote_copy_only_when_asked()
    {
        AddOrigin("merged", "unmerged");

        var kept = await GitService.DeleteBranchAsync(repo.Root, "merged", deleteRemote: false);
        Assert.True(kept.Ok, kept.Message);
        Assert.False(kept.RemoteDeleted);
        Assert.True(OnOrigin("merged"));

        var info = await GitService.BranchDeleteInfoAsync(repo.Root, "unmerged");
        Assert.Equal(("origin", "unmerged", 0), (info.Remote, info.RemoteBranch, info.Lost));
        var both = await GitService.DeleteBranchAsync(repo.Root, "unmerged", deleteRemote: true, info.Tip);

        Assert.True(both.Ok, both.Message);
        Assert.True(both.RemoteDeleted, both.RemoteError);
        Assert.False(OnOrigin("unmerged"));
        Assert.False(HasRef("refs/heads/unmerged"));
        Assert.False(HasRef("refs/remotes/origin/unmerged"));
    }

    [Fact]
    public async Task A_remote_that_fails_leaves_the_local_delete_done_and_says_so()
    {
        AddOrigin("unmerged");
        FileSystemFixtures.DeleteTree(bare!);   // the remote can't be reached
        bare = null;

        var result = await GitService.DeleteBranchAsync(repo.Root, "unmerged", deleteRemote: true);

        Assert.True(result.Ok, result.Message);
        Assert.False(result.RemoteDeleted);
        Assert.NotNull(result.RemoteError);
        Assert.False(HasRef("refs/heads/unmerged"));
    }

    [Fact]
    public async Task A_branch_only_on_the_remote_can_be_deleted_there()
    {
        AddOrigin("unmerged");
        repo.Git("branch", "-q", "-D", "unmerged");   // a teammate's branch: on origin, not here

        var info = await GitService.RemoteBranchDeleteInfoAsync(repo.Root, "origin/unmerged");
        Assert.Equal((true, "origin", "unmerged", false, 2), (info.Exists, info.Remote, info.Branch, info.IsBase, info.Lost));
        Assert.True((await GitService.RemoteBranchDeleteInfoAsync(repo.Root, "origin/main")).IsBase);

        var result = await GitService.DeleteRemoteBranchAsync(repo.Root, "origin/unmerged");

        Assert.True(result.Ok, result.Error);
        Assert.False(OnOrigin("unmerged"));
        Assert.False(HasRef("refs/remotes/origin/unmerged"));
    }

    [Fact]
    public async Task Names_that_are_options_or_not_branches_are_refused_and_never_given_to_git()
    {
        AddOrigin("merged");
        foreach (var name in new[] { "--all", "-D", "no-such-branch", "" })
        {
            Assert.False((await GitService.BranchDeleteInfoAsync(repo.Root, name)).Exists);
            Assert.False((await GitService.DeleteBranchAsync(repo.Root, name, deleteRemote: true)).Ok);
        }
        foreach (var name in new[] { "--all", "origin/--all", "origin/-D", "origin/HEAD", "origin/no-such-branch", "nowhere/merged", "merged" })
        {
            Assert.False((await GitService.RemoteBranchDeleteInfoAsync(repo.Root, name)).Exists);
            Assert.False((await GitService.DeleteRemoteBranchAsync(repo.Root, name)).Ok);
        }
        Assert.True(OnOrigin("merged") && OnOrigin("main") && HasRef("refs/heads/merged"));
    }
}

using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>Git Repository's history: the current branch alone, or every branch, with the parents the graph is drawn from.</summary>
public class CommitGraphTests : IDisposable
{
    private readonly TempRepo repo = new();

    public CommitGraphTests()
    {
        repo.Commit(("a.txt", "base\n"));
        repo.Git("branch", "feature");
        repo.Git("branch", "unmerged");
        repo.Commit(("a.txt", "main\n"));
        repo.Git("checkout", "-q", "feature");
        repo.Commit(("f.txt", "feature\n"));
        repo.Git("checkout", "-q", "unmerged");
        repo.Commit(("u.txt", "unmerged\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-ff", "-m", "merge feature", "feature");
    }

    public void Dispose() => repo.Dispose();

    private string Sha(string rev) => repo.Git("rev-parse", rev).Trim();

    [Fact]
    public async Task Current_branch_leaves_out_unmerged_branches()
    {
        var log = await GitService.GetLogAsync(repo.Root);

        Assert.Equal(4, log.Count);
        Assert.DoesNotContain(log, c => c.Sha == Sha("unmerged"));
    }

    [Fact]
    public async Task All_branches_includes_unmerged_branches()
    {
        var log = await GitService.GetLogAsync(repo.Root, allBranches: true);

        Assert.Equal(5, log.Count);
        Assert.Contains(log, c => c.Sha == Sha("unmerged") && c.Refs.Contains("unmerged"));
    }

    [Fact]
    public async Task Parents_are_listed_first_parent_first_and_children_come_before_parents()
    {
        var log = await GitService.GetLogAsync(repo.Root, allBranches: true);

        var merge = log.Single(c => c.Sha == Sha("main"));
        Assert.Equal([Sha("main^1"), Sha("main^2")], merge.Parents);
        Assert.Empty(log[^1].Parents);   // the root commit
        var index = log.Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i);
        foreach (var c in log)
            foreach (var p in c.Parents)
                Assert.True(index[c.Sha] < index[p], $"{c.Subject} should come before its parent");
    }

    [Fact]
    public async Task A_remotes_HEAD_alias_is_left_out_of_the_badges()
    {
        repo.Git("remote", "add", "origin", "https://example.invalid/repo.git");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        var log = await GitService.GetLogAsync(repo.Root, allBranches: true);

        var head = log.Single(c => c.Sha == Sha("main"));
        // Full names, so the client can tell a local branch from a remote one.
        Assert.Contains("refs/heads/main", head.Refs);
        Assert.Contains("refs/remotes/origin/main", head.Refs);
        Assert.DoesNotContain("origin/HEAD", head.Refs);
    }
}

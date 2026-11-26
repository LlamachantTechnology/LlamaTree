using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>
/// Git Repository's branch focus: one branch's own commits, where it forked and landed, and its net change, for open
/// branches and for ones merged and deleted long ago.
/// </summary>
public sealed class BranchFocusTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly string fork;

    public BranchFocusTests()
    {
        repo.Commit(("main.txt", "1\n"));
        fork = Sha("main");
        // A long branch: two commits, takes in main once, one more commit, then lands as a pull request and is deleted.
        repo.Git("checkout", "-q", "-b", "feature/long", "main");
        repo.Commit(("feature.txt", "a\n"));
        repo.Commit(("feature.txt", "a\nb\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit(("main.txt", "1\n2\n"));
        repo.Git("checkout", "-q", "feature/long");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge branch 'main' into feature/long", "main");
        repo.Commit(("feature.txt", "a\nb\nc\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit(("main.txt", "1\n2\n3\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge pull request #7 from owner/feature/long", "feature/long");
        repo.Git("branch", "-q", "-D", "feature/long");
    }

    public void Dispose() => repo.Dispose();

    private string Sha(string rev) => repo.Git("rev-parse", rev).Trim();

    [Fact]
    public async Task A_merged_and_deleted_branch_is_found_by_name_with_only_its_own_line()
    {
        var focus = await BranchFocusService.GetAsync(repo.Root, null, null, "feature/long");

        Assert.NotNull(focus);
        Assert.Equal(Sha("main"), focus.Landed!.Sha);
        Assert.Equal(fork, focus.Fork!.Sha);
        // Its three commits and the merge from main, newest first; none of main's own commits.
        Assert.Equal(4, focus.Commits.Count);
        var took = Assert.Single(focus.Commits, c => c.FromBase);
        Assert.Equal(1, took.TookIn);
        Assert.Equal("Merge branch 'main' into feature/long", took.Subject);
        Assert.Single(took.Taken!);   // what it brought in, for its folded row
        Assert.Equal(3, focus.Commits.Count(c => c.Parents.Count == 1));
    }

    [Fact]
    public async Task Its_net_change_leaves_out_what_it_took_in_from_main()
    {
        var focus = await BranchFocusService.GetAsync(repo.Root, null, null, "feature/long");

        Assert.Equal(["feature.txt"], focus!.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task An_open_branch_shows_how_far_it_is_from_main()
    {
        repo.Git("checkout", "-q", "-b", "fix/open", "main~1");
        repo.Commit(("fix.txt", "x\n"));
        repo.Git("checkout", "-q", "main");

        var focus = await BranchFocusService.GetAsync(repo.Root, "refs/heads/fix/open", null);

        Assert.Null(focus!.Landed);
        Assert.Equal((1, int.Parse(repo.Git("rev-list", "--count", "main~1..main").Trim())), (focus.Ahead, focus.Behind));
        Assert.Equal(["fix.txt"], focus.Files.Select(f => f.Path));
        Assert.Equal(Sha("main~1"), focus.Fork!.Sha);
    }

    [Fact]
    public async Task A_merged_branch_that_still_exists_finds_where_it_landed()
    {
        repo.Git("checkout", "-q", "-b", "docs/kept", "main");
        repo.Commit(("docs.txt", "d\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge pull request #8 from owner/docs/kept", "docs/kept");
        repo.Commit(("main.txt", "later\n"));

        var focus = await BranchFocusService.GetAsync(repo.Root, "refs/heads/docs/kept", null);

        Assert.Equal(Sha("main~1"), focus!.Landed!.Sha);
        Assert.Equal("d\n", repo.Git("show", focus.Tip + ":docs.txt"));
        Assert.Single(focus.Commits);
    }

    [Fact]
    public async Task A_branch_merged_into_another_opens_by_that_merge_with_its_own_line()
    {
        repo.Git("checkout", "-q", "-b", "feature/base", "main");
        repo.Commit(("base.txt", "1\n"));
        repo.Git("checkout", "-q", "-b", "fix/stacked");
        repo.Commit(("stacked.txt", "s\n"));
        repo.Git("checkout", "-q", "feature/base");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge pull request #9 from owner/fix/stacked", "fix/stacked");
        repo.Git("checkout", "-q", "main");

        var focus = await BranchFocusService.GetAsync(repo.Root, null, Sha("feature/base"));

        Assert.Equal([Sha("fix/stacked")], focus!.Commits.Select(c => c.Sha));
        Assert.Equal(Sha("feature/base~1"), focus.Fork!.Sha);   // it started from feature/base, not main
        Assert.Equal(["stacked.txt"], focus.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Many_branches_at_once_come_out_as_each_would_on_its_own()
    {
        // Plain; taking main in; a rename; one that merged another started from a later main (two places it meets
        // main); one already merged; and an empty one.
        repo.Git("checkout", "-q", "-b", "fix/plain", "main~1");
        repo.Commit(("fix.txt", "x\n"));
        repo.Git("checkout", "-q", "-b", "feature/keeps-up", "main~1");
        repo.Commit(("up.txt", "1\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge branch 'main' into feature/keeps-up", "main");
        repo.Commit(("up.txt", "1\n2\n"));
        repo.Git("checkout", "-q", "-b", "chore/rename", "main");
        repo.Git("mv", "main.txt", "renamed.txt");
        repo.Git("commit", "-q", "-m", "rename");
        repo.Git("checkout", "-q", "-b", "feature/later", "main");
        repo.Commit(("later.txt", "l\n"));
        repo.Git("checkout", "-q", "-b", "feature/two-bases", "main~2");
        repo.Commit(("two.txt", "t\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge branch 'feature/later' into feature/two-bases", "feature/later");
        repo.Git("checkout", "-q", "-b", "docs/merged", "main");
        repo.Commit(("docs.txt", "d\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge pull request #8 from owner/docs/merged", "docs/merged");
        repo.Git("branch", "-q", "empty", "main");

        string[] refs = ["refs/heads/fix/plain", "refs/heads/feature/keeps-up", "refs/heads/chore/rename", "refs/heads/feature/two-bases",
            "refs/heads/docs/merged", "refs/heads/empty"];
        var many = await BranchFocusService.GetManyAsync(repo.Root, [.. refs, "refs/heads/feature", "refs/heads/no-such", "refs/tags/v1", "--all"]);

        Assert.Equal(refs.Order(), many.Keys.Order());   // a ref under "feature" isn't "feature"; the rest don't exist or aren't branches
        foreach (var r in refs)
        {
            var one = await BranchFocusService.GetAsync(repo.Root, r, null);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(one), System.Text.Json.JsonSerializer.Serialize(many[r]));
        }
        Assert.Contains(many["refs/heads/feature/keeps-up"].Commits, c => c.FromBase);
        Assert.Equal("R", Assert.Single(many["refs/heads/chore/rename"].Files).Status);
        Assert.NotNull(many["refs/heads/docs/merged"].Landed);
    }

    [Theory]
    [InlineData(null, "--all", null)]                // not a commit id: never given to git
    [InlineData("refs/tags/v1", null, null)]          // only branches
    [InlineData(null, null, "no/such-branch")]
    public async Task Anything_else_is_not_found(string? refName, string? merge, string? name)
    {
        Assert.Null(await BranchFocusService.GetAsync(repo.Root, refName, merge, name) is { Commits.Count: > 0 } f ? f : null);
    }
}

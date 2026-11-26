using LlamaTree.Git;

namespace LlamaTree.Tests;

/// <summary>Git Repository's Main graph: main's own line, the files each commit on it changes, and the branches still open.</summary>
public sealed class MainLineTests : IDisposable
{
    private readonly TempRepo repo = new();

    public MainLineTests()
    {
        repo.Commit(("a.txt", "1\n"));
        for (var i = 1; i <= 5; i++)
        {
            repo.Git("checkout", "-q", "-b", $"feature/f{i}", "main");
            repo.Commit(($"f{i}.txt", "x\n"));
            repo.Commit(($"f{i}.txt", "x\ny\n"));
            repo.Git("checkout", "-q", "main");
            repo.Git("merge", "-q", "--no-ff", "-m", $"Merge pull request #{i} from owner/feature/f{i}", $"feature/f{i}");
            repo.Git("branch", "-q", "-D", $"feature/f{i}");
        }
        repo.Git("checkout", "-q", "-b", "feature/open", "main~2");   // still open, started two merges back
        repo.Commit(("open.txt", "o\n"));
        repo.Git("checkout", "-q", "main");
    }

    public void Dispose() => repo.Dispose();

    private string Sha(string rev) => repo.Git("rev-parse", rev).Trim();

    [Fact]
    public async Task Lists_mains_own_line_one_row_per_merge_with_what_each_brought_in()
    {
        var line = await MainLineService.GetAsync(repo.Root, 300, []);

        Assert.Equal("main", line.BaseBranch);
        Assert.Equal(6, line.Commits.Count);   // five merges and the first commit; none of the branches' own commits
        Assert.Equal(["f5.txt"], line.Commits[0].Files);   // a merge's files: what it brought in
        Assert.False(line.More);
    }

    [Fact]
    public async Task Gives_every_branch_and_tag_by_the_commit_it_points_at()
    {
        repo.Git("tag", "v1", "main~1");                       // lightweight
        repo.Git("tag", "-a", "-m", "Release 2", "v2", "main");   // annotated: by the commit it tags, not the tag object

        var refs = (await MainLineService.GetAsync(repo.Root, 300, [])).Refs!;

        Assert.Contains("refs/tags/v1", refs[Sha("main~1")]);
        Assert.Equal(["refs/heads/main", "refs/tags/v2"], refs[Sha("main")].Order());
        Assert.Contains("refs/heads/feature/open", refs[Sha("feature/open")]);
    }

    [Fact]
    public async Task Lists_open_branches_with_where_they_started()
    {
        var line = await MainLineService.GetAsync(repo.Root, 300, []);

        var open = Assert.Single(line.Open);
        Assert.Equal(("feature/open", "refs/heads/feature/open", Sha("main~2")), (open.Name, open.Ref, open.Fork));
        Assert.Equal(1, open.Ahead);   // its own commits: what the collapsed row above main counts
    }

    [Fact]
    public async Task Reads_as_far_as_it_is_asked_and_far_enough_for_what_must_show()
    {
        var two = await MainLineService.GetAsync(repo.Root, 2, []);
        Assert.Equal(2, two.Commits.Count);
        Assert.True(two.More);

        // An open lane needs main to reach where it forked: the list goes that far (and a little beyond, if there is any).
        var reach = await MainLineService.GetAsync(repo.Root, 2, [Sha("main~4")]);
        Assert.Contains(reach.Commits, c => c.Sha == Sha("main~4"));
        Assert.Null(await MainLineService.GetAsync(repo.Root, 2, ["--all"]) is { Commits.Count: > 2 } x ? x : null);   // not a commit id: ignored
    }
}

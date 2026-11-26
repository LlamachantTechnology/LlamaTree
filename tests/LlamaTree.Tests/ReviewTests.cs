using LlamaTree.Git;
using LlamaTree.Platform;

namespace LlamaTree.Tests;

/// <summary>
/// Reviewing a branch: its files against main, which you've viewed (until they change), comments that follow their
/// lines as the agent changes the file, and what changed since you last reviewed it.
/// </summary>
public sealed class ReviewTests : IDisposable
{
    private const string Ref = "refs/heads/feature/review";
    private readonly TempRepo repo = new();
    private readonly ReviewStore previous = ReviewService.Store;

    public ReviewTests()
    {
        ReviewService.Store = new ReviewStore(null);
        repo.Commit(("app.js", Lines(1, 10)), ("main.txt", "m\n"));
        repo.Git("checkout", "-q", "-b", "feature/review");
        repo.Commit(("app.js", Lines(1, 10).Replace("line 5\n", "line five\n")), ("new.txt", "new\n"));
    }

    public void Dispose()
    {
        ReviewService.Store = previous;
        repo.Dispose();
    }

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"line {i}\n"));

    private string Sha(string rev) => repo.Git("rev-parse", rev).Trim();

    [Fact]
    public async Task Lists_the_branchs_files_against_main_with_their_counts()
    {
        var r = await ReviewService.GetAsync(repo.Root, Ref);

        Assert.Equal("feature/review", r.Branch);
        Assert.Equal(Sha("main"), r.From);
        Assert.Equal(["app.js", "new.txt"], r.Files.Select(f => f.Path).Order());
        var app = r.Files.Single(f => f.Path == "app.js");
        Assert.Equal(("M", 1, 1), (app.Status, app.Added, app.Removed));
        Assert.Equal(repo.Git("rev-parse", "feature/review:app.js").Trim(), app.Blob);
        Assert.Null(r.Since);   // never reviewed
    }

    [Fact]
    public async Task A_file_marked_viewed_is_remembered_with_its_content()
    {
        var app = (await ReviewService.GetAsync(repo.Root, Ref)).Files.Single(f => f.Path == "app.js");
        await ReviewService.SetViewedAsync(repo.Root, Ref, "app.js", app.Blob);

        // The same review from the branch's remote copy: kept by branch name.
        Assert.Equal(app.Blob, (await ReviewService.GetAsync(repo.Root, Ref)).Viewed["app.js"]);
        await ReviewService.SetViewedAsync(repo.Root, Ref, "app.js", null);
        Assert.Empty((await ReviewService.GetAsync(repo.Root, Ref)).Viewed);
    }

    [Fact]
    public async Task Since_your_review_leaves_out_what_came_in_from_main()
    {
        await ReviewService.MarkReviewedAsync(repo.Root, Ref, [], approve: false);
        var reviewed = Sha("feature/review");
        // Main moves on with a file of its own, the branch takes it in, and then changes new.txt.
        repo.Git("checkout", "-q", "main");
        repo.Commit(("main.txt", "m2\n"));
        repo.Git("checkout", "-q", "feature/review");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge main", "main");
        repo.Commit(("new.txt", "newer\n"));

        var r = await ReviewService.GetAsync(repo.Root, Ref);

        Assert.Equal(reviewed, r.Since!.Tip);
        Assert.Equal(["new.txt"], r.Since.Files.Select(f => f.Path));   // not main.txt
        Assert.Equal((2, 1), (r.Since.Commits, r.Since.BaseCommits));   // the merge and the commit; main's one commit
    }

    [Fact]
    public async Task A_comment_follows_its_line_and_notices_when_the_lines_around_it_change()
    {
        var c = await ReviewService.AddCommentAsync(repo.Root, Ref, "app.js", 8, "line 8", "Why 8?");
        Assert.Equal("pending", c.State);

        // Three lines added at the top: the comment moves down with its line, which hasn't changed.
        repo.Commit(("app.js", "a\nb\nc\n" + Lines(1, 10).Replace("line 5\n", "line five\n")));
        var moved = Assert.Single((await ReviewService.GetAsync(repo.Root, Ref)).Comments);
        Assert.Equal((11, false), (moved.Line, moved.Changed));

        // Sent, then the agent changes that line: it may be addressed.
        await ReviewService.MarkReviewedAsync(repo.Root, Ref, [c.Id], approve: false);
        var sent = Assert.Single((await ReviewService.GetAsync(repo.Root, Ref)).Comments);
        Assert.Equal(("sent", 11, false), (sent.Comment.State, sent.Line, sent.Changed));
        repo.Commit(("app.js", "a\nb\nc\n" + Lines(1, 10).Replace("line 5\n", "line five\n").Replace("line 8\n", "line eight\n")));
        var changed = Assert.Single((await ReviewService.GetAsync(repo.Root, Ref)).Comments);
        Assert.Equal((11, true), (changed.Line, changed.Changed));
    }

    [Fact]
    public async Task Comments_can_be_edited_resolved_and_deleted()
    {
        var c = await ReviewService.AddCommentAsync(repo.Root, Ref, "new.txt", 0, null, "Is this file needed?");
        await ReviewService.EditCommentAsync(repo.Root, Ref, c.Id, "Is this file needed at all?", "resolved");
        var edited = Assert.Single((await ReviewService.GetAsync(repo.Root, Ref)).Comments).Comment;
        Assert.Equal(("Is this file needed at all?", "resolved"), (edited.Text, edited.State));

        await ReviewService.DeleteCommentAsync(repo.Root, Ref, c.Id);
        Assert.Empty((await ReviewService.GetAsync(repo.Root, Ref)).Comments);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewService.AddCommentAsync(repo.Root, Ref, "new.txt", 1, null, "  "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewService.EditCommentAsync(repo.Root, Ref, "nope", "x", null));
    }

    [Fact]
    public async Task Approving_records_the_tip_and_shows_in_the_summary()
    {
        await ReviewService.AddCommentAsync(repo.Root, Ref, "app.js", 5, "line five", "Spell it as a number?");
        await ReviewService.MarkReviewedAsync(repo.Root, Ref, [], approve: true);

        var summary = (await ReviewService.SummariesAsync(repo.Root))["feature/review"];
        Assert.Equal(Sha("feature/review"), summary.ApprovedTip);
        Assert.Equal(summary.ApprovedTip, summary.Tip);   // nothing new since
        Assert.Equal((1, 1), (summary.Pending, summary.Open));
    }

    [Fact]
    public async Task An_opened_review_is_listed_until_forgotten_and_says_when_its_branch_is_merged()
    {
        Assert.Empty(await ReviewService.SummariesAsync(repo.Root));
        await ReviewService.StartAsync(repo.Root, Ref);

        var started = (await ReviewService.SummariesAsync(repo.Root))["feature/review"];
        Assert.Equal((Ref, false, 0), (started.Ref, started.Merged, started.Viewed));
        Assert.NotNull(started.Started);

        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-ff", "-m", "Merge feature/review", "feature/review");
        Assert.True((await ReviewService.SummariesAsync(repo.Root))["feature/review"].Merged);

        await ReviewService.ForgetAsync(repo.Root, "feature/review");
        Assert.Empty(await ReviewService.SummariesAsync(repo.Root));
    }

    [Fact]
    public async Task The_list_offers_branches_with_work_and_no_review_yet()
    {
        repo.Git("checkout", "-q", "-b", "fix/other", "main");
        repo.Commit(("other.txt", "o\n"));
        repo.Git("branch", "-q", "empty", "main");   // nothing main lacks: nothing to review

        var before = await ReviewService.ListAsync(repo.Root);
        Assert.Equal(["feature/review", "fix/other"], before.Unreviewed.Select(c => c.Name).Order());
        Assert.Equal("refs/heads/fix/other", before.Unreviewed.Single(c => c.Name == "fix/other").Ref);
        Assert.True(PathText.SamePlace(repo.Root, before.Repo));

        await ReviewService.StartAsync(repo.Root, Ref);
        var after = await ReviewService.ListAsync(repo.Root);
        Assert.Equal(["fix/other"], after.Unreviewed.Select(c => c.Name));
        Assert.Equal(["feature/review"], after.Reviews.Keys);
    }

    [Theory]
    [InlineData("refs/heads/fix/x", "fix/x")]
    [InlineData("refs/remotes/origin/fix/x", "fix/x")]
    [InlineData("refs/tags/v1", null)]
    [InlineData("--all", null)]
    public void A_branch_is_reviewed_under_its_own_name(string refName, string? branch) =>
        Assert.Equal(branch, ReviewService.BranchOf(refName));

    [Fact]
    public async Task Only_branches_can_be_reviewed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewService.GetAsync(repo.Root, "--all"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewService.GetAsync(repo.Root, "refs/heads/no-such"));
    }
}

using System.Text.Json;
using LlamaTree.Git;
using LlamaTree.Platform;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>
/// Moving a session's folder on disk (Edit session's Folder): a plain folder, a worktree, a repository's main folder, and
/// the agents' saved conversations going with it. Sets CLAUDE_CONFIG_DIR and CODEX_HOME for the whole process, so not run
/// in parallel.
/// </summary>
[Collection(nameof(GitUndoTests))]
public sealed class FolderMoveTests : IDisposable
{
    private readonly string top = Path.Combine(Path.GetTempPath(), "ct-move-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? previousClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    private readonly string? previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");

    public FolderMoveTests()
    {
        Directory.CreateDirectory(top);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(top, "claude"));
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(top, "codex"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaude);
        Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
        try { FileSystemFixtures.DeleteTree(top); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(top, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Line(object o) => JsonSerializer.Serialize(o) + "\n";

    [Fact]
    public void A_move_is_refused_onto_something_already_there_or_into_itself()
    {
        var from = Folder("work");
        Assert.Contains("already exists", FolderMove.Problem(from, Folder("other")));
        Assert.Contains("into itself", FolderMove.Problem(from, Path.Combine(from, "inner")));
        Assert.Contains("full path", FolderMove.Problem(from, "relative"));
        Assert.Contains("isn't there", FolderMove.Problem(Path.Combine(top, "gone"), Path.Combine(top, "new")));
        Assert.Null(FolderMove.Problem(from, Path.Combine(top, "elsewhere", "work")));
    }

    [Fact]
    public void Paths_inside_the_folder_are_rebased_and_others_are_not_inside()
    {
        var from = Path.Combine(top, "work");
        var to = Path.Combine(top, "moved");
        Assert.Equal(to, FolderMove.Rebase(from, from, to));
        Assert.Equal(Path.Combine(to, "src", "app"), FolderMove.Rebase(Path.Combine(from, "src", "app"), from, to));
        Assert.True(FolderMove.Inside(Path.Combine(from, "src"), from));
        Assert.False(FolderMove.Inside(from + "-other", from));   // a sibling whose name starts the same
    }

    [Fact]
    public async Task A_plain_folder_moves_with_its_files_into_a_new_parent()
    {
        var from = Folder("work");
        File.WriteAllText(Path.Combine(from, "a.txt"), "one");
        var to = Path.Combine(top, "new", "parent", "work");
        await FolderMove.MoveAsync(from, to);
        Assert.False(Directory.Exists(from));
        Assert.Equal("one", File.ReadAllText(Path.Combine(to, "a.txt")));
    }

    [Fact]
    public async Task A_worktree_is_moved_by_git_so_the_repository_follows_it()
    {
        using var repo = new TempRepo();
        repo.Commit(("a.txt", "one"));
        var wt = Path.Combine(top, "wt");
        Assert.True((await GitService.AddWorktreeAsync(repo.Root, wt, "feature/x", null)).Ok);
        var to = Path.Combine(top, "moved", "wt");
        await FolderMove.MoveAsync(wt, to);
        Assert.False(Directory.Exists(wt));
        var list = await GitService.GetWorktreesAsync(repo.Root);
        Assert.Contains(list, w => w.Branch == "feature/x" && PathText.SamePlace(w.Path, to) && !w.Prunable);
        Assert.Equal("feature/x", (await GitService.GetStatusAsync(to)).Branch);
    }

    [Fact]
    public async Task A_repository_main_folder_moves_and_its_worktrees_find_it_again()
    {
        using var repo = new TempRepo();   // its folder is moved away, so it has nothing left to delete
        repo.Commit(("a.txt", "one"));
        var main = repo.Root;
        var wt = Path.Combine(top, "wt");
        Assert.True((await GitService.AddWorktreeAsync(main, wt, "feature/y", null)).Ok);
        var to = Path.Combine(top, "main-moved");
        await FolderMove.MoveAsync(main, to);
        // The worktree reaches its repository at the new place.
        Assert.Equal("feature/y", (await GitService.GetStatusAsync(wt)).Branch);
        var list = await GitService.GetWorktreesAsync(wt);
        Assert.True(PathText.SamePlace(list[0].Path, to));
    }

    [Fact]
    public async Task Claude_conversations_of_the_folder_and_folders_inside_it_go_with_it()
    {
        var from = Folder("work");
        var sub = Directory.CreateDirectory(Path.Combine(from, "src")).FullName;
        var projects = ConversationIndex.ProjectsRoot;
        var own = Directory.CreateDirectory(Path.Combine(projects, ConversationIndex.Encode(from))).FullName;
        File.WriteAllText(Path.Combine(own, "c1.jsonl"),
            Line(new { type = "user", cwd = from, sessionId = "c1", message = new { role = "user", content = "hello" } }) + Line(new { type = "user", cwd = sub, sessionId = "c1" }));
        Directory.CreateDirectory(Path.Combine(own, "memory"));
        File.WriteAllText(Path.Combine(own, "memory", "MEMORY.md"), "kept");
        var inner = Directory.CreateDirectory(Path.Combine(projects, ConversationIndex.Encode(sub))).FullName;
        File.WriteAllText(Path.Combine(inner, "c2.jsonl"), Line(new { type = "user", cwd = sub, sessionId = "c2" }));
        // A sibling whose name encodes with the same start isn't touched.
        var sibling = Folder("work-other");
        var siblingProject = Directory.CreateDirectory(Path.Combine(projects, ConversationIndex.Encode(sibling))).FullName;
        File.WriteAllText(Path.Combine(siblingProject, "c3.jsonl"), Line(new { type = "user", cwd = sibling, sessionId = "c3" }));

        var to = Path.Combine(top, "moved");
        await FolderMove.MoveAsync(from, to);
        FolderMove.MoveClaudeConversations(from, to);

        var moved = Path.Combine(projects, ConversationIndex.Encode(to));
        Assert.False(Directory.Exists(own));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(moved, "memory", "MEMORY.md")));
        var cwds = File.ReadAllLines(Path.Combine(moved, "c1.jsonl")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("cwd").GetString()).ToList();
        Assert.Equal([to, Path.Combine(to, "src")], cwds);
        var movedInner = Path.Combine(projects, ConversationIndex.Encode(Path.Combine(to, "src")));
        Assert.True(File.Exists(Path.Combine(movedInner, "c2.jsonl")));
        Assert.True(File.Exists(Path.Combine(siblingProject, "c3.jsonl")));
        Assert.Equal(sibling, JsonDocument.Parse(File.ReadAllText(Path.Combine(siblingProject, "c3.jsonl"))).RootElement.GetProperty("cwd").GetString());
        // Listed for the new folder, as the session there looks for them.
        Assert.Contains(ConversationIndex.List(to, 10), c => c.SessionId == "c1");
    }

    [Fact]
    public void Codex_conversations_of_the_folder_are_listed_for_the_new_one()
    {
        var from = Folder("work");
        var day = Directory.CreateDirectory(Path.Combine(CodexConversations.SessionsRoot, "2026", "11", "10")).FullName;
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(day, $"rollout-2026-11-10T10-00-00-{id}.jsonl"),
            Line(new { type = "session_meta", payload = new { id, cwd = from, timestamp = "2026-11-10T10:00:00Z", source = "cli" } }) +
            Line(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "hello" } } } }));
        var to = Path.Combine(top, "moved");
        Directory.Move(from, to);
        FolderMove.MoveCodexConversations(from, to);
        Assert.Contains(CodexConversations.List(to, 10), c => c.SessionId == id);
        Assert.DoesNotContain(CodexConversations.List(null, 10), c => c.Cwd != null && PathText.SamePlace(c.Cwd, from));
    }
}

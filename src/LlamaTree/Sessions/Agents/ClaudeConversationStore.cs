namespace LlamaTree.Sessions;

/// <summary>
/// Claude Code's conversations for the app: saved in ~/.claude/projects (see <see cref="ConversationIndex"/>), with the
/// one each Claude process is in named in ~/.claude/sessions/&lt;pid&gt;.json (see <see cref="ConversationReader"/>).
/// </summary>
internal sealed class ClaudeConversationStore() : ConversationStore(new NamedByProcess(ConversationReader.ResumableConversation))
{
    public override List<ConversationInfo> List(string? directory, int max) => ConversationIndex.List(directory, max);
    public override ConversationInfo? Find(string id, string directory) => ConversationIndex.Find(id, directory);
    public override bool IsSaved(string id, string directory) => ConversationReader.IsSaved(id, directory);

    /// <summary>claude --continue stops with "No conversation found to continue" in a folder without one.</summary>
    public override bool FolderHasConversation(string directory) => ConversationIndex.HasConversation(directory);

    protected override Conversation? Read(string id, string directory) =>
        ConversationReader.FindTranscript(id, directory) is { } path ? ConversationReader.Read(id, path) : null;

    /// <summary>The one Claude names for its process: it can be in another than the session's own until it has saved it.</summary>
    public override Conversation? ReadLive(TerminalSession session, int[] processIds) => ConversationReader.ForProcesses(processIds);

    public override Conversation? ReadSaved(TerminalSession session) =>
        session.Info.Conversation is { } id ? Read(id, session.Info.Directory) : null;

    public override bool MovesWithFolder => true;
    public override void MoveFolder(string from, string to) => FolderMove.MoveClaudeConversations(from, to);

    /// <summary>From the transcript's usage and compactions (see <see cref="ActivityReader.Context"/>).</summary>
    public override ContextUsage? Context(TerminalSession session)
    {
        if (session.Info.Conversation is not { } id || ConversationReader.FindTranscript(id, session.Info.Directory) is not { } path) return null;
        var usage = ActivityReader.Context(path);
        if (usage.Tokens == 0 && usage.Compactions == 0) return null;
        return usage with { LimitIs = usage.Learned ? "where Claude last compacted on its own" : "the model's window" };
    }
}

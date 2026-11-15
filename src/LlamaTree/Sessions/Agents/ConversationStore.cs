using LlamaTree.Platform;

namespace LlamaTree.Sessions;

/// <summary>
/// An agent's saved conversations, as the rest of the app sees them: the resume picker, the conversation rail, the
/// session's own conversation, and moving a folder. An agent without one isn't followed: its sessions just run.
/// </summary>
internal interface IConversationStore
{
    /// <summary>Its saved conversations, latest first; only those of <paramref name="directory"/> when one is given.</summary>
    List<ConversationInfo> List(string? directory, int max);

    /// <summary>The saved conversation <paramref name="id"/>, or null while the agent hasn't saved anything in it.</summary>
    ConversationInfo? Find(string id, string directory);

    /// <summary>Whether the agent has saved <paramref name="id"/>, so it can be reopened.</summary>
    bool IsSaved(string id, string directory);

    /// <summary>The conversation reopening <paramref name="id"/> opens, or null if none (Codex reopens a sub-thread's parent).</summary>
    string? ResumeTarget(string id) => id;

    /// <summary>Whether <paramref name="directory"/> has a conversation to continue (see <see cref="CommandShape.ContinueNeedsAConversation"/>).</summary>
    bool FolderHasConversation(string directory) => true;

    /// <summary>
    /// The saved conversation the running session's agent is in now, or null when that isn't known. <paramref name="others"/>
    /// are the conversations other sessions are in, which this one can't be in.
    /// </summary>
    string? Live(TerminalSession session, int[] processIds, IReadOnlySet<string> others);

    /// <summary>Whether its conversations can be read as prompts and answers, for the rail and the Conversation tab.</summary>
    bool Shows { get; }

    /// <summary>The conversation the running session's agent is in, as prompts and answers, or null while there's none to show.</summary>
    Conversation? ReadLive(TerminalSession session, int[] processIds);

    /// <summary>The session's own conversation read from disk while it isn't running, or null.</summary>
    Conversation? ReadSaved(TerminalSession session);

    /// <summary>Whether its conversations of a folder can be moved with the folder (<see cref="MoveFolder"/>).</summary>
    bool MovesWithFolder { get; }

    /// <summary>Moves its conversations of <paramref name="from"/> and the folders inside it to <paramref name="to"/>.</summary>
    void MoveFolder(string from, string to) { }

    /// <summary>
    /// How full the session's own conversation is (for the context meter), or null when the agent doesn't say or
    /// nothing has been said in it yet.
    /// </summary>
    ContextUsage? Context(TerminalSession session) => null;
}

/// <summary>How a running session's conversation is found: the part that differs between agents that otherwise store alike.</summary>
internal interface ILiveConversation
{
    /// <summary>The saved conversation the session is in now, or null while that isn't known.</summary>
    string? Find(TerminalSession session, int[] processIds, IReadOnlySet<string> others);
}

/// <summary>Something an agent's files show happened in a conversation: it started, or a prompt was sent in it.</summary>
/// <param name="Folder">The folder the conversation was had in.</param>
internal sealed record ConversationActivity(string Id, string? Folder, DateTime At);

/// <summary>
/// For an agent that can't be asked which conversation its process is in (Codex): the latest one started, or prompted
/// in, in the session's folder since the session started, unless another session is in it.
/// </summary>
/// <param name="recent">The agent's conversation activity since a time (UTC).</param>
internal sealed class LatestInFolder(Func<DateTime, IEnumerable<ConversationActivity>> recent) : ILiveConversation
{
    /// <summary>The session's clock starts just before the agent's own, and some agents write whole seconds.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    public string? Find(TerminalSession session, int[] processIds, IReadOnlySet<string> others) =>
        Pick(recent, session.Info.Directory, session.StartedUtc, others);

    internal static string? Pick(Func<DateTime, IEnumerable<ConversationActivity>> recent, string directory, DateTime startedUtc, IReadOnlySet<string> taken)
    {
        if (PathText.NormalizeFolder(directory) is not { } folder) return null;
        var since = startedUtc - Slack;
        return recent(since)
            .Where(a => a.At >= since && !taken.Contains(a.Id) && PathText.SameFolder(a.Folder, folder))
            .OrderByDescending(a => a.At)
            .Select(a => a.Id)
            .FirstOrDefault();
    }
}

/// <summary>For an agent that records which conversation each of its processes is in (Claude).</summary>
/// <param name="forProcesses">The conversation the agent's process among these is in, once saved.</param>
internal sealed class NamedByProcess(Func<IEnumerable<int>, string?> forProcesses) : ILiveConversation
{
    /// <summary>The agent's own word, so <paramref name="others"/> don't count against it.</summary>
    public string? Find(TerminalSession session, int[] processIds, IReadOnlySet<string> others) => forProcesses(processIds);
}

/// <summary>The first of several ways that knows.</summary>
internal sealed class FirstOf(params ILiveConversation[] ways) : ILiveConversation
{
    public string? Find(TerminalSession session, int[] processIds, IReadOnlySet<string> others) =>
        ways.Select(w => w.Find(session, processIds, others)).FirstOrDefault(id => id != null);
}

/// <summary>
/// What most stores share: the live conversation found by an <see cref="ILiveConversation"/>, and the session's own
/// conversation read for the rail. An agent whose files don't fit can implement <see cref="IConversationStore"/> itself.
/// </summary>
internal abstract class ConversationStore(ILiveConversation live) : IConversationStore
{
    public abstract List<ConversationInfo> List(string? directory, int max);
    public abstract ConversationInfo? Find(string id, string directory);
    public abstract bool IsSaved(string id, string directory);

    /// <summary>Reads the conversation <paramref name="id"/>, or null while the agent hasn't written it.</summary>
    protected abstract Conversation? Read(string id, string directory);

    public virtual string? ResumeTarget(string id) => id;
    public virtual bool FolderHasConversation(string directory) => true;
    public virtual bool Shows => true;
    public virtual bool MovesWithFolder => false;
    public virtual void MoveFolder(string from, string to) { }
    public virtual ContextUsage? Context(TerminalSession session) => null;

    public string? Live(TerminalSession session, int[] processIds, IReadOnlySet<string> others) => live.Find(session, processIds, others);

    /// <summary>The session's own conversation, which the app follows.</summary>
    public virtual Conversation? ReadLive(TerminalSession session, int[] processIds) =>
        session.Info.Conversation is { } id ? Read(id, session.Info.Directory) : null;

    public virtual Conversation? ReadSaved(TerminalSession session) => ReadLive(session, []);
}

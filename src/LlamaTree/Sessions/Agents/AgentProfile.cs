namespace LlamaTree.Sessions;

/// <summary>What a session's start knows, for its agent to build the command line from.</summary>
/// <param name="Conversation">The session's own conversation (<see cref="SessionInfo.Conversation"/>).</param>
/// <param name="ResumeOnce">After an update, the conversation the session was in (see <see cref="TerminalSession.ResumeOnce"/>).</param>
/// <param name="Theme">"light" or "dark": the agent is started in colours that match the terminal, if it can be.</param>
/// <param name="Panels">How the agent reaches the app's panel server, or null when panels are unavailable.</param>
/// <param name="NewConversation">Start a new conversation (a session started from a panel), whatever the session was in.</param>
/// <param name="FirstPrompt">The prompt that new conversation starts with, when it can go on the command line exactly.</param>
internal sealed record Launch(
    string Directory, string? Conversation = null, string? ResumeOnce = null, string? Theme = null, PanelLink? Panels = null,
    bool NewConversation = false, string? FirstPrompt = null);

/// <summary>
/// How the page tells, from the terminal's screen, that an agent is asking you something (JavaScript regex sources, tried
/// on each line on screen).
/// </summary>
/// <param name="Choice">A line of the menu it asks with, such as a picked numbered choice.</param>
/// <param name="PromptRule">With <paramref name="PromptLine"/>: the rule above its prompt box. While the prompt box is on
/// screen it wants a prompt, so menu-like text above it doesn't count.</param>
/// <param name="PromptLine">The first line of its prompt box, under <paramref name="PromptRule"/>.</param>
internal sealed record AskingScreen(string Choice, string? PromptRule = null, string? PromptLine = null);

/// <summary>The colour the page marks an agent's things with (CSS colours), one for each theme so text stays readable.</summary>
internal sealed record AgentColor(string Dark, string Light);

/// <summary>
/// One coding agent's command-line interface, in parts: how its command line reads (<see cref="Command"/>), how it
/// reaches panels (<see cref="Panels"/>), and where its conversations are (<see cref="Conversations"/>). The rest of the
/// app goes through the methods below, which are the same for every agent. Any other command (a shell, a dev server)
/// has no profile and just runs. To support another agent, add a profile to <see cref="Agents.All"/>.
/// </summary>
internal abstract class AgentProfile
{
    /// <summary>The id the page and <see cref="ConversationInfo.Agent"/> use: "claude".</summary>
    public abstract string Id { get; }

    /// <summary>The name shown for it: "Claude".</summary>
    public abstract string Name { get; }

    /// <summary>The program names that start it ("claude", "claude.exe"), compared without case.</summary>
    public abstract IReadOnlyList<string> Programs { get; }

    /// <summary>Its colour, for its tag among the conversations to resume; null leaves the tag plain.</summary>
    public virtual AgentColor? Color => null;

    /// <summary>How the session follows it to another conversation, in words: "through /clear and /resume".</summary>
    public abstract string Follows { get; }

    /// <summary>
    /// How its screen shows it's asking you something, or null. An agent that reports its state itself (an OSC 7501
    /// sequence with state=blocked, as Codex sends) is taken at its word instead, once it has.
    /// </summary>
    public virtual AskingScreen? Asks => null;

    // ---------------------------------------------------------------- its parts

    /// <summary>How its command line reads.</summary>
    public abstract CommandShape Command { get; }

    /// <summary>How it reaches the panel server.</summary>
    public virtual PanelConnection Panels => PanelConnection.None;

    /// <summary>Its saved conversations, or null when the app doesn't follow them (its sessions just run as typed).</summary>
    public virtual IConversationStore? Conversations => null;

    /// <summary>Anything only this agent adds to an interactive start (Claude's theme --settings), after the first prompt.</summary>
    public virtual IEnumerable<string> ExtraArguments(Launch launch, ParsedCommand parsed) => [];

    // ---------------------------------------------------------------- one-off prompts

    /// <summary>
    /// A command that answers one prompt read from standard input with plain text on standard output, changing nothing and
    /// saving no conversation (Suggest writes commit messages with it), or null.
    /// </summary>
    public virtual string? HeadlessCommand => null;

    /// <summary>
    /// Whether <see cref="HeadlessCommand"/> runs with no tools at all, so it sees only the prompt it's given and can't read
    /// a file (claude -p --tools ""). A read-only sandbox isn't enough: codex exec -s read-only still reads any file.
    /// </summary>
    public virtual bool HeadlessHasNoTools => false;

    // ---------------------------------------------------------------- the same for every agent

    /// <summary>
    /// Whether the app picks a new conversation's id and starts the agent in it (claude --session-id). Otherwise the
    /// agent picks it, and the session learns it from <see cref="LiveConversation"/>.
    /// </summary>
    public bool ChoosesConversationId => Command.NewConversationWithId != null && Conversations != null;

    /// <summary>
    /// Whether the app can look after this command's conversation (the session's <see cref="SessionInfo.Conversation"/>):
    /// the interactive interface, with no options that pick a conversation of their own.
    /// </summary>
    public bool CanTrackConversation(string command) => Conversations != null && ParsedCommand.Parse(Command, command).CanTrack;

    /// <summary>Whether starting this command again carries on a conversation it picks itself, rather than starting a new one.</summary>
    public bool CarriesOnConversation(string command) => ParsedCommand.Parse(Command, command).CarriesOn;

    /// <summary>
    /// The command without the option that resumes a particular conversation, and that conversation, so the session keeps
    /// it itself: "claude --resume X --model opus" becomes ("claude --model opus", X). Anything else comes back unchanged, with no id.
    /// </summary>
    public (string Command, string? Conversation) TakeConversation(string command) =>
        Conversations == null ? (command, null) : ParsedCommand.Parse(Command, command).TakeConversation();

    /// <summary>The command without an option that carries on the folder's latest conversation (the app picks it instead).</summary>
    public string StripContinue(string command) => ParsedCommand.StripContinue(Command, command);

    /// <summary>The command line for this start of <paramref name="command"/> (see <see cref="CommandBuilder.Build"/>).</summary>
    public string CommandLine(string command, Launch launch) => CommandBuilder.Build(this, command, launch, new StoreFacts(Conversations, launch.Directory));

    /// <summary>Anything the agent needs in its environment for this start (e.g. the panel token).</summary>
    public void AddEnvironment(IDictionary<string, string> env, Launch launch) => Panels.AddEnvironment(env, launch.Panels);

    /// <summary>Whether it reads the panel server from a file written for the session (Claude's --mcp-config).</summary>
    public bool PanelsFromFile => Panels.NeedsConfigFile;

    /// <inheritdoc cref="IConversationStore.Live"/>
    public string? LiveConversation(TerminalSession session, int[] processIds, IReadOnlySet<string> others) =>
        Conversations?.Live(session, processIds, others);

    /// <inheritdoc cref="IConversationStore.List"/>
    public List<ConversationInfo> ListConversations(string? directory, int max) => Conversations?.List(directory, max) ?? [];

    /// <inheritdoc cref="IConversationStore.Find"/>
    public ConversationInfo? FindConversation(string id, string directory) => Conversations?.Find(id, directory);

    /// <inheritdoc cref="IConversationStore.Shows"/>
    public bool ShowsConversation => Conversations?.Shows == true;

    /// <inheritdoc cref="IConversationStore.ReadLive"/>
    public Conversation? ReadConversation(TerminalSession session, int[] processIds) => Conversations?.ReadLive(session, processIds);

    /// <inheritdoc cref="IConversationStore.ReadSaved"/>
    public Conversation? ReadSavedConversation(TerminalSession session) => Conversations?.ReadSaved(session);

    /// <summary>What the page needs to know about it.</summary>
    public object Describe() => new
    {
        id = Id, name = Name, programs = Programs, color = Color, choosesId = ChoosesConversationId, follows = Follows,
        ownConversation = Command.OwnConversationPattern, @continue = Command.ContinuePattern, showsConversation = ShowsConversation, asks = Asks,
        suggests = HeadlessCommand != null,
    };

    /// <summary>The start's questions answered from the agent's store (or, without one, as for a conversation never saved).</summary>
    private sealed class StoreFacts(IConversationStore? store, string directory) : IConversationFacts
    {
        public bool IsSaved(string id) => store?.IsSaved(id, directory) == true;
        public string? ResumeTarget(string id) => store == null ? null : store.ResumeTarget(id);
        public bool FolderHasConversation() => store?.FolderHasConversation(directory) != false;
    }
}

/// <summary>The agents the app knows.</summary>
internal static class Agents
{
    public static readonly ClaudeAgent Claude = new();
    public static readonly CodexAgent Codex = new();
    public static readonly AntigravityAgent Antigravity = new();
    public static readonly GrokAgent Grok = new();

    public static IReadOnlyList<AgentProfile> All { get; } = [Claude, Codex, Antigravity, Grok];

    /// <summary>The agent <paramref name="command"/> starts, from its first word, or null for any other command.</summary>
    public static AgentProfile? For(string? command)
    {
        var program = (command ?? "").Trim().Split(' ', 2)[0];
        return All.FirstOrDefault(a => a.Programs.Contains(program, StringComparer.OrdinalIgnoreCase));
    }
}

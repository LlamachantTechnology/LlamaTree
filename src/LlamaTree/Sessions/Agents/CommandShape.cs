using System.Text.RegularExpressions;
using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// How an agent reopens a conversation by id: an option ("--resume &lt;id&gt;", added after the rest of the command) or
/// a subcommand ("resume &lt;id&gt;", straight after the program). The first name is the one the app writes.
/// </summary>
internal sealed record ResumeSyntax(IReadOnlyList<string> Names, bool IsSubcommand = false);

/// <summary>
/// An agent's command line, as data: which words and options mean what. <see cref="ParsedCommand"/> reads a command with
/// it and <see cref="CommandBuilder"/> builds each start's command line from it, the same way for every agent.
/// </summary>
internal sealed record CommandShape
{
    /// <summary>Subcommands: anything but the interactive interface, except <see cref="OwnConversationSubcommands"/>.</summary>
    public IReadOnlySet<string> Subcommands { get; init; } = new HashSet<string>();

    /// <summary>
    /// Whether a subcommand counts wherever it appears in the command (codex -m o3 exec), not only straight after the
    /// program.
    /// </summary>
    public bool SubcommandAnywhere { get; init; }

    /// <summary>Options that print one answer instead of running the interactive interface (-p).</summary>
    public IReadOnlyList<string> PrintOptions { get; init; } = [];

    /// <summary>Options that carry on the folder's latest conversation (-c, --continue).</summary>
    public IReadOnlyList<string> ContinueOptions { get; init; } = [];

    /// <summary>Whether the agent stops when told to continue in a folder with no conversation yet, so the option is dropped then.</summary>
    public bool ContinueNeedsAConversation { get; init; }

    /// <summary>How it reopens a conversation by id.</summary>
    public required ResumeSyntax Resume { get; init; }

    /// <summary>Options with which the command picks its own conversation (besides <see cref="Resume"/>): --session-id, --fork-session.</summary>
    public IReadOnlyList<string> OwnConversationOptions { get; init; } = [];

    /// <summary>Subcommands that run the interactive interface in a conversation they pick (codex resume, codex fork).</summary>
    public IReadOnlySet<string> OwnConversationSubcommands { get; init; } = new HashSet<string>();

    /// <summary>The option that starts a new conversation with an id the app picks (claude --session-id), or null when the agent picks it.</summary>
    public string? NewConversationWithId { get; init; }

    /// <summary>The option a first prompt follows (agy -i), or null when it's an argument straight after the program.</summary>
    public string? FirstPromptOption { get; init; }

    /// <summary>A JavaScript regex source for the options with which a command picks its own conversation (the page notes and strips them).</summary>
    public required string OwnConversationPattern { get; init; }

    /// <summary>A JavaScript regex source for <see cref="ContinueOptions"/>, or null when it has none.</summary>
    public string? ContinuePattern =>
        ContinueOptions.Count == 0 ? null : @"\s+(?:" + string.Join("|", ContinueOptions.Select(Regex.Escape)) + @")(?=\s|$)";

    private Regex? takeResume;

    /// <summary>Finds "--resume &lt;id&gt;", "--resume=&lt;id&gt;" or "resume &lt;id&gt;" with a conversation id (see <see cref="TerminalSession.IsConversationId"/>).</summary>
    internal Regex TakeResume => takeResume ??= new Regex(
        @"\s+(?:" + string.Join("|", Resume.Names.Select(Regex.Escape)) + ")" + (Resume.IsSubcommand ? @"\s+" : @"(?:=|\s+)") +
        "(" + TerminalSession.ConversationIdPattern + @")(?=\s|$)", RegexOptions.IgnoreCase);
}

/// <summary>A command read with its agent's <see cref="CommandShape"/>.</summary>
internal sealed class ParsedCommand
{
    private readonly IReadOnlyList<string> options;

    private ParsedCommand(CommandShape shape, string command)
    {
        Shape = shape;
        Command = command;
        Tokens = Split(command);
        options = Tokens.Skip(1).Select(t => t.Split('=')[0]).ToList();
        var words = shape.SubcommandAnywhere ? Tokens.Skip(1) : Tokens.Skip(1).Take(1);
        Subcommand = words.FirstOrDefault(w => shape.Subcommands.Contains(w) || shape.OwnConversationSubcommands.Contains(w))?.ToLowerInvariant();
    }

    public static ParsedCommand Parse(CommandShape shape, string command) => new(shape, command);

    public CommandShape Shape { get; }
    public string Command { get; }
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>The subcommand it runs (lowercase), or null for the interactive interface.</summary>
    public string? Subcommand { get; }

    /// <summary>Whether it has one of these options, written alone or as option=value, ignoring case.</summary>
    public bool Has(params IEnumerable<string> names) => options.Any(o => names.Contains(o, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether it runs the interactive interface: no subcommand but one that picks a conversation, and not one printed answer.</summary>
    public bool IsInteractive =>
        (Subcommand == null || Shape.OwnConversationSubcommands.Contains(Subcommand)) && !Has(Shape.PrintOptions);

    /// <summary>Whether it picks its own conversation: --resume and the like, or codex resume.</summary>
    public bool PicksOwnConversation =>
        (Subcommand != null && Shape.OwnConversationSubcommands.Contains(Subcommand)) ||
        Has(Shape.OwnConversationOptions) || (!Shape.Resume.IsSubcommand && Has(Shape.Resume.Names));

    /// <summary>Whether the app can look after its conversation: the interactive interface, picking none of its own.</summary>
    public bool CanTrack => IsInteractive && !PicksOwnConversation;

    /// <summary>Whether starting it again carries on a conversation it picks itself: --continue, or reopening one.</summary>
    public bool CarriesOn =>
        Has(Shape.ContinueOptions) ||
        (Shape.Resume.IsSubcommand ? Subcommand != null && Shape.Resume.Names.Contains(Subcommand, StringComparer.OrdinalIgnoreCase) : Has(Shape.Resume.Names));

    /// <summary>
    /// The command without the option that reopens a conversation, and that conversation, so the session keeps it
    /// itself: "claude --resume X --model opus" becomes ("claude --model opus", X). Anything else comes back unchanged, with no id.
    /// </summary>
    public (string Command, string? Conversation) TakeConversation()
    {
        var m = Shape.TakeResume.Match(Command);
        if (!m.Success || !TerminalSession.IsConversationId(m.Groups[1].Value)) return (Command, null);
        var rest = (Command[..m.Index] + Command[(m.Index + m.Length)..]).Trim();
        return Parse(Shape, rest).CanTrack ? (rest, m.Groups[1].Value) : (Command, null);
    }

    /// <summary>The command without the options that carry on the folder's latest conversation.</summary>
    public string StripContinue() => StripContinue(Shape, Command);

    internal static string StripContinue(CommandShape shape, string command) =>
        shape.ContinuePattern is { } pattern ? Regex.Replace(command.Trim(), pattern, "", RegexOptions.IgnoreCase) : command;

    internal static string[] Split(string command) => command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The command with <paramref name="argument"/> straight after the program.</summary>
    internal static string AfterProgram(string command, string argument)
    {
        var program = Split(command)[0];
        var trimmed = command.Trim();
        return program + " " + argument + trimmed[program.Length..];
    }
}

/// <summary>What the start needs to know about the agent's saved conversations to decide what to reopen.</summary>
internal interface IConversationFacts
{
    /// <summary>Whether the agent has saved <paramref name="id"/>, so it can be reopened.</summary>
    bool IsSaved(string id);

    /// <summary>The conversation reopening <paramref name="id"/> opens (Codex: a sub-thread's parent), or null if none.</summary>
    string? ResumeTarget(string id);

    /// <summary>Whether the folder has a conversation to continue.</summary>
    bool FolderHasConversation();
}

/// <summary>Builds each start's command line from the agent's <see cref="CommandShape"/>, the same way for every agent.</summary>
internal static class CommandBuilder
{
    /// <summary>
    /// The command line for this start of <paramref name="command"/>. Anything but the agent's interactive interface is
    /// left exactly as it is. A command that picks its own conversation keeps it; otherwise, in this order:
    /// - the session's own conversation: reopened once the agent has saved it, or before then started with that id if
    ///   the app can pick it (claude --session-id); a session started from a panel uses it only if the app picked it;
    /// - a session started from a panel: a new conversation, so any --continue is dropped;
    /// - after an update, the conversation it was in (<see cref="Launch.ResumeOnce"/>), once saved;
    /// - --continue in a folder with nothing to continue, for an agent that would stop: dropped.
    /// Then the first prompt (unless a conversation was reopened), the agent's own arguments, and the panel server.
    /// </summary>
    public static string Build(AgentProfile agent, string command, Launch launch, IConversationFacts facts)
    {
        if (Agents.For(command)?.Id != agent.Id) return command;
        var shape = agent.Command;
        var parsed = ParsedCommand.Parse(shape, command);
        if (!parsed.IsInteractive) return command;

        var result = command.Trim();
        var reopened = false;
        if (!parsed.PicksOwnConversation)
        {
            var own = TerminalSession.IsConversationId(launch.Conversation) ? launch.Conversation : null;
            var reopen = own != null && (shape.NewConversationWithId != null || !launch.NewConversation) ? facts.ResumeTarget(own) : null;
            if (reopen != null && facts.IsSaved(reopen))
                (result, reopened) = (Resume(shape, result, reopen), true);
            else if (own != null && shape.NewConversationWithId != null)
                result = ParsedCommand.StripContinue(shape, result) + " " + shape.NewConversationWithId + " " + own;
            else if (launch.NewConversation)
                result = ParsedCommand.StripContinue(shape, result);
            else if (launch.ResumeOnce is { } once && TerminalSession.IsPlainId(once) && facts.ResumeTarget(once) is { } then && facts.IsSaved(then))
                (result, reopened) = (Resume(shape, result, then), true);
            else if (shape.ContinueNeedsAConversation && parsed.Has(shape.ContinueOptions) && !facts.FolderHasConversation())
                result = ParsedCommand.StripContinue(shape, result);
        }
        if (launch.FirstPrompt != null && !reopened && parsed.Subcommand == null)
            result = ParsedCommand.AfterProgram(result,
                (shape.FirstPromptOption is { } option ? option + " " : "") + CommandLineResolver.QuoteArgument(launch.FirstPrompt));
        foreach (var argument in agent.ExtraArguments(launch, parsed))
            result = result.TrimEnd() + " " + argument;
        if (launch.Panels != null) result = agent.Panels.AddTo(result, parsed, launch.Panels);
        return result;
    }

    private static string Resume(CommandShape shape, string command, string id) =>
        shape.Resume.IsSubcommand
            ? ParsedCommand.AfterProgram(command, shape.Resume.Names[0] + " " + id)
            : ParsedCommand.StripContinue(shape, command) + " " + shape.Resume.Names[0] + " " + id;
}

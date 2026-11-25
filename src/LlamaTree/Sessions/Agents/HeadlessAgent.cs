using System.Diagnostics;
using System.Text;
using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// Runs an agent once, headless, with a prompt on its standard input and its answer on standard output
/// (<see cref="AgentProfile.HeadlessCommand"/>: claude -p with no tools, codex exec read-only, agy -p, grok -p or opencode run). It
/// runs with the user's own sign-in, in the same clean environment sessions get. The commit message suggester and the
/// plugins' ask request use it (ask with a tool-less agent when one is installed, see <see cref="SessionAssistant.WriterFor"/>).
/// </summary>
internal static class HeadlessAgent
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The agents that can answer, best first: those with no tools (Claude), then those that save no conversation (Codex),
    /// then the rest (Antigravity, Grok, OpenCode), each group in <see cref="Agents.All"/> order.
    /// </summary>
    internal static IReadOnlyList<AgentProfile> Writers { get; } = Agents.All.Where(a => a.HeadlessCommand != null)
        .OrderByDescending(a => a.HeadlessHasNoTools).ThenBy(a => a.HeadlessSavesConversation).ToList();

    /// <summary>
    /// The agent that answers for a session: its own when it can and is installed, else the first of <see cref="Writers"/>
    /// that's installed, so a session with no agent, or whose agent isn't installed here, still gets an answer.
    /// </summary>
    public static AgentProfile Writer(AgentProfile? agent) => Writer(agent, IsInstalled);

    /// <param name="installed">Whether an agent's headless command can be run here.</param>
    internal static AgentProfile Writer(AgentProfile? agent, Func<AgentProfile, bool> installed) =>
        agent?.HeadlessCommand != null && installed(agent) ? agent
        : Writers.FirstOrDefault(installed)
          ?? (agent?.HeadlessCommand != null ? agent : Agents.Claude);   // none installed: running it says what wasn't found

    /// <summary>Who writes for a session with no agent of its own that can: the first installed, checked once a run.</summary>
    public static string FallbackName => fallback.Value.Name;
    private static readonly Lazy<AgentProfile> fallback = new(() => Writer(null));

    /// <summary>The folder a run that saves a conversation goes in (<see cref="AgentProfile.HeadlessSavesConversation"/>): the app's own, empty.</summary>
    internal static string ScratchFolder()
    {
        var folder = Path.Combine(AppProfile.CustomFolder ?? AppProfile.LocalFolder, "headless");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Whether the agent's headless command can be run: its program is on the PATH sessions get.</summary>
    public static bool IsInstalled(AgentProfile agent) => agent.HeadlessCommand is { } command && CanRun(command);

    /// <summary>Whether a session could start the agent: its program ("claude") is on the PATH sessions get.</summary>
    public static bool CanStart(AgentProfile agent) => CanRun(agent.Programs[0]);

    private static bool CanRun(string command)
    {
        var env = SessionEnvironment.Build();
        try
        {
            if (OperatingSystem.IsWindows()) CommandLineResolver.Resolve(command, env);
            else CommandLineResolver.ResolveArgv(command, env);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (InvalidOperationException) { return true; }   // found, but can't be run safely: RunAsync says why
    }

    /// <param name="dir">
    /// The folder to run in: one the user already trusts, such as a session's. An agent that saves what it's asked runs in
    /// <see cref="ScratchFolder"/> instead, so no session in <paramref name="dir"/> takes the run for its conversation.
    /// </param>
    /// <param name="task">What the agent was asked to do, for the error when it can't: "suggest a message", "answer".</param>
    public static async Task<string> RunAsync(AgentProfile agent, string dir, string prompt, string task, CancellationToken ct)
    {
        var where = agent.HeadlessSavesConversation ? ScratchFolder() : dir;
        var output = await RunCommandAsync(agent, agent.HeadlessCommand!, where, agent.HeadlessInput(prompt), task, ct);
        try { return agent.HeadlessAnswer(output); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{agent.Name} couldn't {task}: {ex.Message}"); }
    }

    /// <summary>Runs another of the agent's own commands (such as "agy mcp list") the same way, with <paramref name="input"/> on its standard input.</summary>
    public static async Task<string> RunCommandAsync(AgentProfile agent, string command, string dir, string input, string task, CancellationToken ct)
    {
        var env = SessionEnvironment.Build();
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            if (OperatingSystem.IsWindows())
                (psi.FileName, psi.Arguments) = SplitProgram(CommandLineResolver.Resolve(command, env));
            else
            {
                var argv = CommandLineResolver.ResolveArgv(command, env);   // /bin/sh -c "<command>"
                psi.FileName = argv[0];
                foreach (var a in argv[1..]) psi.ArgumentList.Add(a);
            }
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"{agent.Name} ({agent.Programs[0]}) wasn't found on PATH, so it can't {task}.");
        }
        // The same clean environment sessions get, not this app's (which may have been started from a Claude session).
        psi.Environment.Clear();
        foreach (var (name, value) in env) psi.Environment[name] = value;

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Timeout);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {agent.Name}.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = process.StandardError.ReadToEndAsync(timer.Token);
            await process.StandardInput.WriteAsync(input.AsMemory(), timer.Token);
            process.StandardInput.Close();
            var output = await stdout;
            var error = await stderr;
            await process.WaitForExitAsync(timer.Token);
            if (process.ExitCode != 0)
            {
                var why = (error.Trim().Length > 0 ? error : output).Trim();
                throw new InvalidOperationException($"{agent.Name} couldn't {task}: " + (why.Length > 400 ? why[..400] + "…" : why));
            }
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{agent.Name} didn't answer within {Timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>CommandLineResolver gives a whole command line; the program is its first (possibly quoted) token.</summary>
    internal static (string Program, string Arguments) SplitProgram(string line)
    {
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return (line[1..end], line[(end + 1)..].TrimStart());
        }
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }
}

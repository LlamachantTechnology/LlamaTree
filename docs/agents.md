# AI agents

LlamaTree works with these AI agents today: Claude Code, Codex, Antigravity, Grok and OpenCode. Any other command runs in a session too.

A session's command decides what it runs: `claude` for [Claude Code](https://docs.anthropic.com/en/docs/claude-code), `codex` for OpenAI's [Codex CLI](https://github.com/openai/codex), `agy` for Google's Antigravity CLI, `grok` for xAI's Grok CLI, `opencode` for [OpenCode](https://opencode.ai) (each with any options you add), or anything else, such as a shell (`cmd` on Windows, `zsh` on macOS and Linux, and `bash` on all of them, suggested by the Command box; or `pwsh`, `fish` and the like, typed in) or a dev server. They can sit side by side, in the same project or even the same folder.

## What each gets

| | Claude Code | Codex | Antigravity | Grok | OpenCode | Any other command |
|---|---|---|---|---|---|---|
| **Sessions and window** | | | | | | |
| Tiles, tabs, projects, shortcuts | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Working, waiting, idle and not running dots | ✓ | ✓ | ✓ | ✓ | ✓ from OpenCode itself, not its screen | ✓ |
| *Needs your answer* dot | ✓ permissions, questions, plans, the folder trust check | ✓ approvals and questions (Codex reports them), the folder trust check | ✓ its menus: commands to approve, the folder trust check | ✓ commands to approve | ✓ permissions and questions (read from OpenCode itself) | with [OSC 7501](#other-agents-the-needs-your-answer-dot) |
| Starts when you first click on its terminal, after the app restarts | ✓ | ✓ | ✓ | ✓ | ✓ | No, starts straight away |
| **Conversations** | | | | | | |
| A conversation of its own, even several in one folder | ✓ the app picks its id | ✓ learned once Codex saves it | ✓ learned once you send its first prompt | ✓ the app picks its id | ✓ learned once it works in one | No |
| Reopens it when the app or the session restarts | ✓ `--resume <id>` | ✓ `codex resume <id>` | ✓ `--conversation <id>` | ✓ `--resume <id>` | ✓ `--session <id>` | No |
| Follows you to another conversation | ✓ `/clear`, `/resume` | ✓ `/new`; `/resume` once you send a prompt in it | ✓ once you send a prompt in it | ✓ `/new`, `/resume` | ✓ `/new`, `/sessions` once you send a prompt in it | No |
| Resume picker | ✓ | ✓ tagged *Codex* | ✓ tagged *Antigravity* | ✓ tagged *Grok* | ✓ tagged *OpenCode* | No |
| Conversation title on hover, and in the closing question | ✓ | ✓ | ✓ | ✓ | ✓ | No |
| **Conversation rail and tab** | | | | | | |
| Rail of prompts and the Conversation tab | ✓ | ✓ | ✓ | ✓ | ✓ | No |
| Works in full-screen mode | ✓ | ✓ | ✓ | ✓ | ✓ | No |
| Project view: latest prompt and answer | ✓ | ✓ | ✓ | ✓ | ✓ | status only |
| **Panels and messages** | | | | | | |
| Panels beside the terminal, their buttons, and messages between sessions | ✓ | ✓ | ✓ | ✓ | ✓ | No |
| **Theme** | | | | | | |
| App, terminal and diff colours, accent, themes | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| The agent itself started in matching light or dark colours | ✓ | not yet | not yet | not yet | not yet | No |
| **Git** | | | | | | |
| Git Changes, Git Repository, diffs, fetch/pull/push, pull requests, Project view | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **Suggest** writes the commit message | ✓ Claude Code | ✓ Codex | ✓ Antigravity | ✓ Grok | ✓ OpenCode | ✓ the first installed of Claude Code, Codex, Antigravity, Grok and OpenCode |
| **App** | | | | | | |
| Plugins | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Updates: sessions carry on in their conversations | ✓ | ✓ | ✓ | ✓ | ✓ | restarted |

## Claude Code

Each Claude session is started with:

- `--session-id` or `--resume`, so it keeps its own conversation (see [Each session has its own conversation](sessions.md#each-session-has-its-own-conversation));
- `--mcp-config` pointing at LlamaTree's MCP server for [panels and messages](panels.md) (your own MCP servers still load; a command that passes its own `--mcp-config` is left alone);
- `--settings` with the app's light or dark theme, since Claude can't detect the terminal's colours on Windows (left alone if you pass your own).

The conversation rail and tab read Claude's saved transcript, so they work in Claude's full-screen mode too. **Suggest** runs Claude Code as `claude -p` (see [Suggest](git.md#suggest)).

## Codex

- **Its own conversation.** Codex can't be told which conversation id to use, so a new session learns its conversation once Codex has saved it: the latest one started in its folder since the session started, not counting `codex exec` runs. From then on it's reopened with `codex resume <id>`. A Guardian review thread Codex starts under a conversation opens that conversation.
- **Following you.** After `/new` the session moves to the new conversation once Codex has saved it. After `/resume` it moves to the conversation you picked once you send a prompt in it, read from Codex's prompt history (`~/.codex/history.jsonl`). With `history.persistence = "none"` in Codex's config, `/resume` isn't followed.
- **Saved conversations** are read from `~/.codex/sessions`, or `CODEX_HOME` when it's set.
- **Panels and messages:** the app adds its MCP server to Codex's command line (`-c mcp_servers.llamatree.…`), with the session's token in an environment variable rather than on the command line. A command that sets that server itself is left alone.
- **Needs your answer:** Codex reports in the terminal when it's waiting on you, so the dot doesn't depend on reading its screen (see [below](#other-agents-the-needs-your-answer-dot)).
- **Suggest** runs `codex exec --ephemeral -s read-only` with low reasoning: read-only and not saved as a conversation, using a little of your Codex plan.
- **Commands that choose for themselves:** `codex resume …` and `codex fork …` keep their own choice of conversation, and Codex's other subcommands (`codex exec`, say) run as they are.

## Antigravity

- **Its own conversation.** agy can't be told which conversation id to use, so a new session learns its conversation once you send its first prompt: the latest one prompted in its folder since the session started. From then on it's reopened with `agy --conversation <id>`.
- **Following you.** When you switch to another conversation in agy, the session moves to it once you send a prompt in it.
- **Saved conversations** are read from `~/.gemini/antigravity-cli`: each conversation's transcript, the title agy gave it, and the folder of the prompt it started with (from agy's prompt history). Moving a session's folder doesn't move its agy conversations, since agy also keeps their folder in databases of its own.
- **Panels and messages:** agy can't be given an MCP server for one session, so before its first session LlamaTree adds itself to agy's MCP servers (`agy mcp add llamatree <LlamaTree> mcp-bridge`) unless it's there already. That server finds the session from the session's environment, and offers nothing to an agy started outside LlamaTree. Each session also starts with `--settings` allowing LlamaTree's tools without asking, keeping your own permissions from agy's `settings.json` (which is only read). A command that passes its own `--settings` is left alone.
- **Commands that choose for themselves:** `agy --conversation <id>` keeps its choice, and `agy -p` and agy's subcommands run as they are.
- **Suggest** runs `agy -p` with low effort and slash commands off, the diff sent as stream-json on its standard input. agy has no way to save nothing, so each run is kept as an agy conversation; it runs in a folder of LlamaTree's own (`headless` in its local data folder), so no session takes it for its conversation.

## Grok

- **Its own conversation.** As with Claude, the app picks a new session's conversation id (`--session-id`) and reopens it with `grok --resume <id>`.
- **Following you.** The session follows `/new` and `/resume` through the conversation grok records for its process (`~/.grok/active_sessions.json`).
- **Saved conversations** are read from `~/.grok/sessions`, or `GROK_HOME` when it's set. `grok -p` runs aren't listed. Moving a session's folder doesn't move its Grok conversations yet.
- **Panels and messages:** as for agy, LlamaTree adds itself to grok's MCP servers once (`grok mcp add llamatree -- <LlamaTree> mcp-bridge`), and each session starts with `--allow mcp__llamatree` so LlamaTree's tools don't ask each time.
- **Commands that choose for themselves:** `grok --resume …`, `--session-id` and `--fork-session` keep their own choice, and `grok -p` and grok's subcommands run as they are.
- **Suggest** runs `grok -p` with every tool denied (`--deny "*"`), no web search and no subagents, the diff on its standard input. As for agy, it runs in LlamaTree's own folder, since grok keeps each run.

## OpenCode

OpenCode keeps its conversations in a database of its own. LlamaTree doesn't open it: it asks OpenCode instead.

- **Its own conversation.** OpenCode can't be told which conversation id to use, but its interface serves an HTTP API on this computer. Each session starts it with `--port` on a free port, and a password of the session's own (`OPENCODE_SERVER_PASSWORD`) so nothing else can use it. The session follows OpenCode's events there and takes the conversation it last started working in (not a subagent's) for its own. From then on it's reopened with `opencode --session <id>`.
- **Following you.** After `/new` or `/sessions` the session moves to that conversation once you send a prompt in it.
- **Needs your answer and working:** read from OpenCode's events (a permission or a question waiting, the conversation busy or idle), not from its screen.
- **Conversation rail, tab and context meter** are read from the running OpenCode. A stopped session's conversation, the resume picker and conversation titles come from `opencode export` and `opencode db`, which take most of a second each, so LlamaTree asks again only once OpenCode's database (`~/.local/share/opencode/opencode.db`, or `XDG_DATA_HOME`/`OPENCODE_DB`) has changed. The picker shows OpenCode's conversations once OpenCode has run on this computer.
- **Panels and messages:** LlamaTree adds its MCP server through `OPENCODE_CONFIG_CONTENT`, which OpenCode merges with its own config, with the session's token taken from the environment (`{env:LLAMATREE_PANEL_TOKEN}`) and its tools (`llamatree_*`) allowed without asking. Config of your own in that variable is kept, and so is an MCP server named `llamatree` you set up yourself.
- **Commands that choose for themselves:** `opencode --session …` and `--continue` keep their own choice, `--port` keeps its port (with `--port 0` the session can't follow OpenCode), and `opencode run` and OpenCode's other subcommands run as they are.
- **Suggest** runs `opencode run --format json`, the diff on its standard input. It runs with OpenCode's tools: with every tool denied or turned off, OpenCode never answers. As for agy, it runs in LlamaTree's own folder, since OpenCode keeps each run.

LlamaTree only adds its MCP server to agy or grok when it's missing, or when the LlamaTree it names has been moved or deleted, so two copies of LlamaTree don't keep changing it. To take it out, run `agy mcp remove llamatree` or `grok mcp remove llamatree`; LlamaTree adds it again the next time it starts a session of that agent.

## Other agents: the needs your answer dot

Any program can report that it's blocked on you by writing an `OSC 7501` sequence with `state=blocked` to the terminal, which is how Codex does it. LlamaTree shows the pink dot for it, without reading the screen.

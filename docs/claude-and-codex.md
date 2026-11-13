# Claude Code, Codex and Antigravity

A session's command decides what it runs: `claude` for [Claude Code](https://docs.anthropic.com/en/docs/claude-code), `codex` for OpenAI's [Codex CLI](https://github.com/openai/codex), `agy` for Google's Antigravity CLI (each with any options you add), or anything else, such as a shell or a dev server. They can sit side by side, in the same project or even the same folder.

## What each gets

| | Claude Code | Codex | Antigravity (agy) | Any other command |
|---|---|---|---|---|
| **Sessions and window** | | | | |
| Tiles, tabs, projects, shortcuts | ✓ | ✓ | ✓ | ✓ |
| Working, waiting, idle and not running dots | ✓ | ✓ | ✓ | ✓ |
| *Needs your answer* dot | ✓ permissions, questions, plans, the folder trust check | ✓ approvals and questions (Codex reports them), the folder trust check | not yet | with [OSC 7501](#other-agents-the-needs-your-answer-dot) |
| Starts when you first click on its terminal, after the app restarts | ✓ | ✓ | ✓ | No, starts straight away |
| **Conversations** | | | | |
| A conversation of its own, even several in one folder | ✓ the app picks its id | ✓ learned once Codex saves it | ✓ learned once agy saves it | No |
| Reopens it when the app or the session restarts | ✓ `--resume <id>` | ✓ `codex resume <id>` | ✓ `--conversation <id>` | No |
| Follows you to another conversation | ✓ `/clear`, `/resume` | ✓ `/new`; `/resume` once you send a prompt in it | ✓ a new one, or one picked with `/resume`, once you send a prompt in it | No |
| Resume picker | ✓ | ✓ tagged *Codex* | ✓ tagged *Antigravity* | No |
| Conversation title on hover, and in the closing question | ✓ | ✓ | ✓ | No |
| **Conversation rail and tab** | | | | |
| Rail of prompts and the Conversation tab | ✓ | ✓ | ✓ | No |
| Works in full-screen mode | ✓ | ✓ | ✓ | No |
| Project view: latest prompt and answer | ✓ | ✓ | ✓ | status only |
| **Panels and messages** | | | | |
| Panels beside the terminal, their buttons, and messages between sessions | ✓ | ✓ | ✓ [through a bridge](#antigravity-agy) | No |
| **Theme** | | | | |
| App, terminal and diff colours, accent, themes | ✓ | ✓ | ✓ | ✓ |
| The agent itself started in matching light or dark colours | ✓ | not yet | not yet | No |
| **Git** | | | | |
| Git Changes, Git Repository, diffs, fetch/pull/push, pull requests, Project view | ✓ | ✓ | ✓ | ✓ |
| **Suggest** writes the commit message | ✓ Claude Code | ✓ Codex | ✓ Claude Code, if installed | ✓ Claude Code, if installed |
| **App** | | | | |
| Plugins | ✓ | ✓ | ✓ | ✓ |
| Updates: sessions carry on in their conversations | ✓ | ✓ | ✓ | restarted |

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

## Antigravity (agy)

- **Panels and messages.** `agy` can't be given an MCP server for one start, so before its first session starts in a run LlamaTree checks `agy mcp list` and, if there's no `llamatree` server (or it points at another copy of LlamaTree), runs `agy mcp add llamatree "<LlamaTree's program>" mcp-bridge`. The server it adds has no port or token in it: each session's environment (`LLAMATREE_PANEL_URL`, `LLAMATREE_PANEL_TOKEN`) tells it where to connect, and started outside LlamaTree it offers no tools. Turn it off with `agy mcp disable llamatree`: a disabled server is left alone.
- **The panel tools don't ask each time.** Each session starts with `--settings` allowing `mcp(llamatree/*)`, as Claude's sessions allow `mcp__llamatree`. The file is your own `permissions` from `~/.gemini/antigravity-cli/settings.json` (which is only read) with that rule added, so your other rules still apply. `agy` uses it for that session only and never saves it; a command that passes its own `--settings` is left alone.
- **Its own conversation.** `agy` can't be told which conversation id to use, so a new session learns its conversation once `agy` has saved it: the latest one started in its folder since the session started. From then on it's reopened with `--conversation <id>`, in place of any `-c`. A command with its own `--conversation <id>` hands that conversation to the session.
- **Following you.** A new conversation is followed once you send its first prompt; one picked with `/resume` (or carried on with `-c`) once you send a prompt in it.
- **Saved conversations** are read from `~/.gemini/antigravity-cli`, without `agy`'s databases: the transcript in `brain/<id>/.system_generated/logs`, the title from `annotations/<id>.pbtxt`, and the folder from the prompt history, `history.jsonl` (the only place `agy` records it). So a conversation `agy -p` ran, which isn't in the prompt history, isn't listed, and a conversation whose prompts aren't in it can't be told apart by folder.
- **The rail and the Conversation tab** read the transcript: your prompts, `agy`'s answers and the tools it ran. Tool output and messages from other sessions aren't shown.
- **Not yet:** **Suggest** uses Claude Code, since `agy -p` can't read its prompt from standard input and saves a conversation of each run. Moving a session's folder doesn't move its `agy` conversations, which `agy` keeps the folder of in its own databases; they're still listed under the old folder.
- A panel's button that starts an `agy` session gives it the prompt with `-i`, so the session carries on afterwards.

## Other agents: the needs your answer dot

Any program can report that it's blocked on you by writing an `OSC 7501` sequence with `state=blocked` to the terminal, which is how Codex does it. LlamaTree shows the pink dot for it, without reading the screen.

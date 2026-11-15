// Draws a static Codex, Antigravity (agy) or Grok screen for README screenshots, as fake-claude.mjs does for Claude, so
// no real session, account or conversation ever appears in them. Run through the demo's codex.cmd, agy.cmd and grok.cmd
// (see setup-demo.mjs) as `<agent> <scenario>`, so the app takes each for that agent and adds what it adds to its
// command line (Codex's -c mcp_servers…, agy's --settings, grok's --session-id and --allow), which this ignores.
//   codex payments  -> keeps "working", and says so as Codex does (OSC 7501)
//   agy docs        -> finished, idle at its prompt
//   grok tooling    -> works for a few seconds, then asks to run a command
// Anything else the app runs (agy mcp list, grok mcp add, codex exec, -p) answers nothing and exits, so the real
// agents' settings are never touched.

const [agent, ...args] = process.argv.slice(2);
const scenario = args[0] && !args[0].startsWith('-') ? args[0] : null;
if (!scenario || ['mcp', 'exec', 'help', 'login', 'models'].includes(scenario) || args.some((a) => ['-p', '--print', '--prompt'].includes(a))) process.exit(0);

const out = (s) => process.stdout.write(s);
const ESC = '\x1b[';
const rgb = (r, g, b) => (s) => `${ESC}38;2;${r};${g};${b}m${s}${ESC}39m`;
const bgRgb = (r, g, b) => (s) => `${ESC}48;2;${r};${g};${b}m${s}${ESC}49m`;
const bold = (s) => `${ESC}1m${s}${ESC}22m`;
const gray = rgb(153, 153, 153);
const green = rgb(78, 186, 101);
const red = rgb(230, 105, 105);
const cyan = rgb(97, 214, 214);
const agyGreen = rgb(52, 168, 83);
const agyBlue = rgb(66, 133, 244);
const white = rgb(230, 230, 230);
const addBg = bgRgb(34, 74, 40);
const delBg = bgRgb(92, 34, 44);
const visible = (s) => s.replace(/\x1b\[[0-9;]*m/g, '');
const pad = (s, w) => s + ' '.repeat(Math.max(0, w - visible(s).length));
const WIDTH = 100;   // fixed, as in fake-claude.mjs: the screenshot terminals are always wider

function box(lines, color, width) {
  return [
    color('╭' + '─'.repeat(width - 2) + '╮'),
    ...lines.map((l) => color('│ ') + pad(l, width - 4) + color(' │')),
    color('╰' + '─'.repeat(width - 2) + '╯'),
  ];
}

const scenarios = {
  // Codex, refactoring payments: its header box, a prompt, an edit and a test run, then "Working".
  codex: {
    payments: {
      title: 'Refactor payment intents',
      lines: [
        ...box([bold('>_ OpenAI Codex'), '', gray('directory: ') + '~/acme-shop-payments'], gray, 52),
        '',
        cyan('› ') + 'Replace the legacy billing module with Stripe PaymentIntents and keep the tests green',
        '',
        '• I\'ll move the payments route onto PaymentIntents, drop the legacy client, then run the payments tests.',
        '',
        '• ' + bold('Edited') + ' src/routes/payments.ts ' + gray('(') + green('+19') + ' ' + red('-23') + gray(')'),
        '    ' + gray('8 ') + delBg(pad("-import { chargeCard } from '../lib/legacy-billing';", 60)),
        '    ' + gray('8 ') + addBg(pad("+import { stripe } from '../lib/stripe';", 60)),
        '',
        '• ' + bold('Deleted') + ' src/lib/legacy-billing.ts',
        '',
        '• ' + bold('Ran') + ' npm test -- payments',
        gray('  └ ') + green('✓') + ' creates a PaymentIntent for the cart total ' + gray('(41 ms)'),
        '    ' + green('✓') + ' rejects an empty cart ' + gray('(3 ms)'),
        '    ' + green('✓') + ' passes the idempotency key through ' + gray('(5 ms)'),
        '',
      ],
      working: (secs, frame) => [(frame % 2 ? '◦' : '•') + ' ' + bold('Working') + gray(` (${secs} • esc to interrupt)`)],
      prompt: () => ['', cyan('› ') + gray('Ask Codex to do anything'), '', gray('  ⏎ send   ⌃J newline   ⌃T transcript   ⌃C quit')],
      reports: true,
    },
  },

  // Antigravity, done with the deployment guide and waiting at its prompt box.
  agy: {
    docs: {
      title: 'Deployment guide',
      lines: [
        ' ' + agyBlue('▲') + ' ' + bold('Antigravity'),
        '   ' + gray('~/acme-shop-docs'),
        '',
        '> Write a deployment guide for Fly.io and link it from the README',
        '',
        '  ' + agyGreen('✓') + ' Created ' + bold('docs/deploy.md') + gray(' (+58)'),
        '  ' + agyGreen('✓') + ' Edited ' + bold('README.md') + gray(' (+4)'),
        '',
        '  Added ' + bold('docs/deploy.md') + ': secrets, the Postgres attachment and zero-downtime deploys,',
        '  linked from the README under "Deploying". Ready for review.',
        '',
      ],
      prompt: () => [gray('─'.repeat(WIDTH)), '> ', gray('─'.repeat(WIDTH)), gray('  ? for shortcuts')],
      done: true,
    },
  },

  // Grok, adding a pre-commit hook: reads, then asks before running a command.
  grok: {
    tooling: {
      title: 'Pre-commit hook',
      lines: [
        ' ' + bold(white('Grok')) + '  ' + gray('~/acme-shop'),
        '',
        gray('> ') + 'Add a pre-commit hook that runs the linter and the type check',
        '',
        green('●') + ' Read package.json',
        green('●') + ' Read tsconfig.json',
        '',
        '  I\'ll set up husky with a pre-commit hook that runs ' + bold('npm run lint') + ' and ' + bold('npm run typecheck') + '.',
        '',
      ],
      working: (secs, frame) => [white(['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'][frame % 10]) + ' Thinking' + gray(` ${secs}`)],
      prompt: () => ['', gray('> '), '', gray(' Shift+Tab:mode  Ctrl+C:quit')],
      workSeconds: 7,
      asking: [
        ' ' + bold('Run command?'),
        '   ' + white('npx husky init && echo "npm run lint && npm run typecheck" > .husky/pre-commit'),
        '',
        ' ' + cyan('❯ 1. Yes'),
        '   2. Yes, and don\'t ask again for npx husky',
        '   3. No, tell Grok what to do instead',
        '',
        gray(' 1/3:select  Enter:confirm  Esc:cancel'),
      ],
    },
  },
};

const s = scenarios[agent]?.[scenario];
if (!s) {
  console.error(`Unknown ${agent} scenario "${scenario}".`);
  process.exit(1);
}

// Codex says what it's doing (OSC 7501), so the app shows it as working without guessing from the output.
const report = (state) => { if (s.reports) out(`\x1b]7501;state=${state}:app=codex\x07`); };

const started = Date.now() - 72_000;   // look like it has been working for a while
let frame = 0;
let phase = s.done ? 'done' : 'working';

function tail() {
  if (phase === 'done') return s.prompt();
  if (phase === 'waiting') return s.asking;
  const secs = Math.floor((Date.now() - started) / 1000);
  return [...s.working(secs >= 60 ? `${Math.floor(secs / 60)}m ${String(secs % 60).padStart(2, '0')}s` : `${secs}s`, frame++), ...s.prompt()];
}

// From where drawAll saved the cursor, as in fake-claude.mjs.
const drawTail = () => out(`\x1b8${ESC}J` + tail().join('\r\n'));

function drawAll() {
  out('\x1b[?25l' + `${ESC}2J${ESC}3J${ESC}H` + `\x1b]0;${s.title}\x07`);
  out(s.lines.join('\r\n') + '\r\n\x1b7');
  drawTail();
}

drawAll();
// Once the resizing stops: a redraw for each step of a resize would look like a burst of work to the app.
let resized;
process.stdout.on('resize', () => { clearTimeout(resized); resized = setTimeout(drawAll, 300); });
// The resize event comes late, and a screen drawn at the old width leaves lines behind once the terminal reflows it:
// draw it all again now and then while it works, so a tile never keeps them (and quiet stays quiet).
setInterval(() => { if (phase === 'working') drawAll(); }, 1000);
report(phase === 'working' ? 'working' : 'idle');

if (phase === 'working') {
  const timer = setInterval(() => { drawTail(); report('working'); }, s.reports ? 500 : 120);
  if (s.workSeconds) {
    setTimeout(() => {
      clearInterval(timer);
      phase = 'waiting';
      drawTail();
      report('blocked');
    }, s.workSeconds * 1000);
  }
}

// Stay alive (and quiet) until the app closes the session.
setInterval(() => {}, 1 << 30);

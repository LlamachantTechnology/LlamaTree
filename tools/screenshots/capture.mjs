// Drives a running LlamaTree instance (started with --remote-debugging-port) through each README
// scene and saves PNGs. Usage: node capture.mjs <port> <outDir> <demoRoot> [siteDir]
// With siteDir, it also saves the website's home-page shots there (PNGs, for converting to its WebP files).
import fs from 'node:fs';
import path from 'node:path';

const port = Number(process.argv[2] ?? 9333);
const outDir = path.resolve(process.argv[3] ?? 'docs/images');
const demoRoot = process.argv[4];
const siteDir = process.argv[5] && path.resolve(process.argv[5]);
if (!demoRoot) throw new Error('Usage: node capture.mjs <port> <outDir> <demoRoot> [siteDir]');
const WIDTH = 1600, HEIGHT = 900, SCALE = 1.5;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function connect() {
  for (let i = 0; i < 60; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = targets.find((t) => t.type === 'page' && t.url.startsWith('https://app.local/'));
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* app still starting */ }
    await sleep(500);
  }
  throw new Error(`No LlamaTree page on port ${port}`);
}

const ws = new WebSocket(await connect());
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0;
const waiting = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  waiting.get(m.id)?.(m);
  waiting.delete(m.id);
};
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const id = ++seq;
  waiting.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
  ws.send(JSON.stringify({ id, method, params }));
});

async function js(expression) {
  const r = await send('Runtime.evaluate', { expression: `(async () => { ${expression} })()`, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}

// clip: a part of the page (CSS pixels) and how much to enlarge it, rather than the whole window.
async function shot(name, { dir = outDir, clip } = {}) {
  await sleep(1300);   // over a second: a working fake redraws in full each second, after a resize has reflowed it
  const { data } = await send('Page.captureScreenshot', { format: 'png', ...(clip && { clip }) });
  const file = path.join(dir, `${name}.png`);
  fs.writeFileSync(file, Buffer.from(data, 'base64'));
  console.log(`saved ${path.relative(process.cwd(), file)}`);
}

fs.mkdirSync(outDir, { recursive: true });
// The page appears before its script has run: wait until the app's functions exist.
for (let i = 0; ; i++) {
  const r = await send('Runtime.evaluate', { expression: `typeof setFontSize === 'function' && typeof state === 'object'`, returnByValue: true });
  if (r.result.value === true) break;
  if (i > 120) throw new Error('The app page never finished loading');
  await sleep(250);
}
await send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE, mobile: false });
// A slightly larger terminal font keeps the scaled-down tile previews readable (demo profile only).
await js('setFontSize(17);');
// The same look whatever the PC: dark, with the app's own purple rather than this Windows' accent colour.
await js(`setThemePref('dark'); accent.choice = 'purple'; store.set('accentColour', 'purple'); applyAccent();`);
// Only the demo's conversations: agy's are always read from this PC's own ~/.gemini (Claude's, Codex's and Grok's come
// from the demo's folders), so any conversation outside the demo is left out of every list.
await js(`
  const root = ${JSON.stringify(demoRoot.toLowerCase().replace(/\\/g, '/').replace(/\/?$/, '/'))};
  const own = request;
  request = (type, payload) => own(type, payload).then((r) => type === 'conversations' && Array.isArray(r)
    ? r.filter((c) => (c.cwd || '').toLowerCase().replace(/\\\\/g, '/').startsWith(root)) : r);`);

// Let every session start, the "working" ones spin, and web-frontend reach its permission prompt.
// Saved Claude sessions start when they're first opened; here they all start at once.
await js(`
  for (let i = 0; i < 60 && state.sessions.length < 6; i++) await new Promise((r) => setTimeout(r, 500));
  for (const s of state.sessions) if (s.startsOnOpen) startSession(s);
  for (let i = 0; i < 60 &&!(state.sessions.length === 6 && state.sessions.every((s) => s.running && s.git)); i++)
    await new Promise((r) => setTimeout(r, 500));
`);
await sleep(13_000);

const reset = `
  closeMenu(); if (!$('#modalHost').hidden) closeModal();
  for (const t of [...docs.tabs]) closeDoc(t.key);
  showTerminalDoc(); collapse();
  resetDock();   // one group of side-panel tabs, Git Changes open
  if (state.gitWidth !== 400) { state.gitWidth = 400; applyGitVisibility(); layout(true); }
  showAllProjects();
  for (const n of notices.list) notices.dismissed.add(n.id);   // no tip, offer or news along the top (not saved)
  renderNotice();
`;

// 1. Overview: Inventory unticked to show the project toggles; the other projects and Unknown at once.
console.log('states:', await js(`${reset} toggleProject('inv'); showGit('checkout');
  return state.sessions.map((s) => s.name + '=' + statusClass(s)).join(', ');`));
await shot('overview');

// 1b. The context meter, close up: checkout-api's conversation is past half (amber), with the meter's menu open.
await js(`${reset} showGit('checkout');
  const s = state.byId.get('checkout');
  for (let i = 0; i < 40 && !s.ctx; i++) await new Promise((r) => setTimeout(r, 250));
  const r = s.header.querySelector('.chip.ctx').getBoundingClientRect();
  contextMenu(s, r.left, r.bottom + 2);`);
await shot('context-meter', { clip: { x: 0, y: 0, width: 800, height: 450, scale: 1.2 } });
await js('closeMenu();');

// 2. One session expanded with its Git Changes.
await js(`${reset} expand('checkout');
  const row = [...document.querySelectorAll('#gitPanel .row')].find((r) => r.textContent.startsWith('webhooks.ts'));
  row?.classList.add('selected');`);
await shot('session');

// 3. Side-by-side diff in a document tab next to the terminal.
await js(`${reset} expand('payments');
  const s = state.byId.get('payments');
  openDiff(s, { mode: 'unstaged', path: 'tests/payments.test.ts', status: 'M' }, true);
  await new Promise((r) => setTimeout(r, 1500));
  openDiff(s, { mode: 'unstaged', path: 'src/routes/payments.ts', status: 'M' }, true);
  await new Promise((r) => setTimeout(r, 2500));`);
await shot('diff');

// 4. Git Repository's Focused graph: main with its merges, the session's branch open (it opens by itself), one merged
// branch and one open branch opened too, and a commit selected.
await js(`${reset} expand('checkout');
  const s = state.byId.get('checkout');
  state.gitWidth = 720; applyGitVisibility(); layout(true);   // wide enough for the graph's lines and its badges
  const wait = async (ok) => { for (let i = 0; i < 50 && !ok(); i++) await new Promise((r) => setTimeout(r, 200)); };
  state.repoView = 'graph'; store.set('repoView', 'graph'); setGraphScope('main');
  s.drill = null; activateTab(dock.groups[0], 'repo');
  const dr = () => drillState(s);
  await wait(() => dr().data && [...dr().expanded.values()].some((e) => e.data));
  const merge = dr().data.commits.find((c) => /feature\\/stripe-client/.test(c.subject));
  if (merge) await expandBranch(s, { merge: merge.sha, name: 'feature/stripe-client' }, { select: false, scroll: false });
  await expandBranch(s, { ref: 'refs/heads/refactor/payments', name: 'refactor/payments' }, { select: false, scroll: false });
  const tip = dr().expanded.get('refs/heads/feature/stripe-webhooks')?.data?.commits[0];
  if (tip) await selectCommit(s, tip.sha);
  document.querySelector('.repo-log.drill').scrollTop = 0;
  await new Promise((r) => setTimeout(r, 500));`);
await shot('history');

// 4b. In flight: each branch not merged yet as a card.
await js(`state.repoView = 'inflight'; store.set('repoView', 'inflight'); renderGit();
  const s = state.byId.get('checkout');
  for (let i = 0; i < 100 && !(s.inflight && !s.inflightLoading); i++) await new Promise((r) => setTimeout(r, 200));
  renderGit();
  await new Promise((r) => setTimeout(r, 1000));`);
await shot('in-flight');
await js(`state.repoView = 'graph'; store.set('repoView', 'graph'); renderGit();`);

// 4c. A review of feature/stripe-webhooks against main: one file ticked off and a comment waiting to be sent on
// orders.ts, with the Reviews tab beside it (cart-redesign's review started too, so it lists more than one).
await js(`${reset} expand('checkout');
  const wait = async (ok) => { for (let i = 0; i < 50 && !ok(); i++) await new Promise((r) => setTimeout(r, 200)); };
  const s = state.byId.get('checkout');
  state.gitWidth = 440; applyGitVisibility(); layout(true);
  await request('git.reviewUpdate', { id: 'frontend', ref: 'refs/heads/feature/cart-redesign', op: 'start' });
  openReview(s, 'refs/heads/feature/stripe-webhooks', 'feature/stripe-webhooks');
  const tab = docs.tabs.find(isReviewTab);
  await wait(() => tab.data);
  const env = tab.data.files.find((f) => f.path === '.env.example');
  if (env) setViewed(tab, env, true);
  tab.file = 'src/orders.ts'; renderReview(tab);
  const model = () => review.editor?.getModel()?.modified;
  await wait(() => model()?.getValue().includes('markOrderPaid'));
  const line = model().getLinesContent().findIndex((l) => l.includes('function markOrderPaid')) + 1;
  tab.draft = { path: 'src/orders.ts', line, text: 'This can run twice if Stripe retries the webhook. Ignore an order that is already paid.' };
  await addComment(tab);
  review.editor.revealLineInCenter(line);
  activateTab(dock.groups[0], 'reviews');
  await loadAllReviews(true);
  await new Promise((r) => setTimeout(r, 1500));`);
await shot('review');
if (siteDir) await shot('review', { dir: siteDir });
await js(`for (const t of docs.tabs.filter(isReviewTab)) closeDoc(t.key); activateTab(dock.groups[0], 'changes');`);

// 5. Resuming a saved conversation (fake conversations from the demo's CLAUDE_CONFIG_DIR).
await js(`${reset} showGit('checkout');
  showAddDialog('resume');
  for (let i = 0; i < 20 && !document.querySelector('.conv-item'); i++) await new Promise((r) => setTimeout(r, 200));
  const pick = document.querySelectorAll('.conv-item:not(.open)')[0];
  pick?.click();
  document.querySelector('.conv-item.selected')?.scrollIntoView({ block: 'center' });`);   // one that isn't already open in a session
await shot('resume');

// 6. The conversation rail beside the open terminal, with the hover card for an earlier prompt (shown as hovering does).
await js(`${reset} expand('checkout');
  const s = state.byId.get('checkout');
  for (let i = 0; i < 40 && !(s.conv && s.conv.turns.length > 3); i++) await new Promise((r) => setTimeout(r, 250));
  await new Promise((r) => setTimeout(r, 500));
  s.rail.children[2].classList.add('hot');
  showRailCard(s.rail, 2);`);
await shot('rail');
await js(`hideRailCard(); document.querySelector('.chat-rail i.hot')?.classList.remove('hot');`);

// 8. A panel Claude showed: My tasks, grouped by status, with ticks, badges, times and buttons.
await js(`${reset} expand('checkout');
  for (let i = 0; i < 40 && !state.panelsLoaded; i++) await new Promise((r) => setTimeout(r, 200));
  state.gitWidth = 560; applyGitVisibility(); layout(true);   // refit now: a same-frame width change isn't seen by the resize observer
  activateTab(groupOfTab('panel:checkout/my-tasks'), 'panel:checkout/my-tasks');`);
await shot('panels');

// 9. Docking: Git Changes on top, the errors panel below it (stat tiles, a trend, details with coloured code), and the
// My tasks tab marked as updated.
await js(`${reset} expand('checkout');
  state.gitWidth = 560; applyGitVisibility(); layout(true);   // refit now: a same-frame width change isn't seen by the resize observer
  const errors = panelByTab('panel:checkout/errors');
  setFolded(errors, 'd:ERR-1042', false);
  moveTab('panel:checkout/errors', { kind: 'below', group: dock.groups[0] });
  moveTab('panel:checkout/my-tasks', { kind: 'join', group: dock.groups[1], before: null });
  dock.groups[0].size = 0.42; dock.groups[1].size = 0.58;
  activateTab(dock.groups[1], 'panel:checkout/errors');
  panelSeen.set('panel:checkout/my-tasks', 'earlier'); renderGit();
  await new Promise((r) => setTimeout(r, 2500));`);   // Monaco colours the code block meanwhile
await shot('docking');

// 10. Two sessions side by side, a diff in one's pane while the other asks for permission.
await js(`${reset} openPanes(['checkout', 'frontend'], 'checkout');
  await new Promise((r) => setTimeout(r, 1500));   // both redraw at their pane's width
  openDiff(state.byId.get('checkout'), { mode: 'unstaged', path: 'src/server.ts', status: 'M' }, true);
  await new Promise((r) => setTimeout(r, 2500));`);
await shot('side-by-side');
await js(`${reset} state.views = []; saveViews(); renderSessionTabs();`);   // the shots after it have no view

// The website's home page: the same demo in its own framings.
if (siteDir) {
  fs.mkdirSync(siteDir, { recursive: true });
  // Every session as a tile, with the projects and their sessions down the left.
  await js(`${reset} showGit('checkout');`);
  await sleep(3000);   // the tiles have been resized: let their screens settle
  await shot('layout-left-tiles', { dir: siteDir });
  // Projects as tabs across the top, and no side panel.
  await js(`${reset} setNavLayout('top'); setProjectTabs(true); if (state.gitVisible) toggleGit();`);
  await sleep(3000);
  await shot('layout-project-tabs', { dir: siteDir });
  await js(`if (!state.gitVisible) toggleGit(); setProjectTabs(false);`);
  // Still across the top, with projects to tick: a side-by-side diff of payments-refactor in a tab of its own.
  await js(`${reset}
    const s = state.byId.get('payments');
    expand('payments');
    openDiff(s, { mode: 'unstaged', path: 'tests/payments.test.ts', status: 'M' }, true);
    await new Promise((r) => setTimeout(r, 1500));
    openDiff(s, { mode: 'unstaged', path: 'src/routes/payments.ts', status: 'M' }, true);
    await new Promise((r) => setTimeout(r, 2500));`);
  await shot('diff', { dir: siteDir });
  // Only Acme Shop ticked: checkout-api's server.ts diff docked left of its terminal, and Git Changes stacked over
  // the errors panel.
  await js(`${reset} for (const g of ['docs', 'inv', UNKNOWN]) toggleProject(g);
    expand('checkout');
    state.gitWidth = 470; applyGitVisibility(); layout(true);
    openDiff(state.byId.get('checkout'), { mode: 'unstaged', path: 'src/server.ts', status: 'M' }, true);
    await new Promise((r) => setTimeout(r, 1500));
    splitDoc(docs.active, 'left');
    setFolded(panelByTab('panel:checkout/errors'), 'd:ERR-1042', true);   // before the move, which draws it
    moveTab('panel:checkout/errors', { kind: 'below', group: dock.groups[0] });
    dock.groups[0].size = 0.48; dock.groups[1].size = 0.52;
    activateTab(dock.groups[0], 'changes'); activateTab(dock.groups[1], 'panel:checkout/errors');
    await new Promise((r) => setTimeout(r, 2500));`);
  await shot('small-docking', { dir: siteDir });
  await js(`splitDoc(docs.active, null); showAllProjects(); setNavLayout('left');`);
  // New session's list of saved conversations, each agent's, around the one picked.
  const box = await js(`${reset} showGit('checkout');
    showAddDialog('resume');
    for (let i = 0; i < 20 && !document.querySelector('.conv-item'); i++) await new Promise((r) => setTimeout(r, 200));
    // Codex's, so the ones around it are of other agents.
    const items = [...document.querySelectorAll('.conv-item:not(.open)')];
    (items.find((e) => e.textContent.includes('Codex')) ?? items[0])?.click();
    document.querySelector('.conv-item.selected')?.scrollIntoView({ block: 'center' });
    const m = document.querySelector('#modalHost .modal').getBoundingClientRect();
    const list = document.querySelector('#modalHost .conv-picker').getBoundingClientRect();
    return { x: m.left - 24, y: list.top - 40 };`);
  await shot('small-resume', { dir: siteDir, clip: { x: box.x, y: box.y, width: 808, height: 455, scale: 1 } });
  fs.copyFileSync(path.join(outDir, 'context-meter.png'), path.join(siteDir, 'context-meter.png'));
}

// 7. The light theme, on the Conversation tab: the whole conversation as text, opened at an earlier prompt.
await js(`${reset} setThemePref('light'); if (!$('#modalHost').hidden) closeModal();   // not "Update Claude's colours?"
  expand('checkout');
  openConversation(state.byId.get('checkout'), 1);
  for (let i = 0; i < 20 && !document.querySelector('.chat-turn.filled'); i++) await new Promise((r) => setTimeout(r, 250));
  await new Promise((r) => setTimeout(r, 800));`);
await shot('conversation');

await js(`${reset} setThemePref('dark'); panelSeen.clear();`);
await send('Emulation.clearDeviceMetricsOverride');
ws.close();

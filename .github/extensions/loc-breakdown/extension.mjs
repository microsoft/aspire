// Extension: loc-breakdown
// Canvas showing lines-of-code changes grouped by project/category, with a
// heuristic characterization (mechanical / detailed / careful review) per group.

import { createServer } from "node:http";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { joinSession, createCanvas } from "@github/copilot-sdk/extension";

const execFileP = promisify(execFile);
const servers = new Map(); // instanceId -> { server, url, opts }

// ---------- git plumbing ----------

async function git(cwd, args) {
    const { stdout } = await execFileP("git", args, {
        cwd,
        maxBuffer: 64 * 1024 * 1024,
    });
    return stdout;
}

// Reject anything that could be parsed by git as an option, contain a path
// traversal into another revision range, or smuggle shell-like control chars.
// git's own rules already forbid most of these in real ref names
// (https://git-scm.com/docs/git-check-ref-format), so this is conservative.
// We allow `A...B` / `A..B` range syntax because callers may pass those.
function assertSafeRef(value, label) {
    if (typeof value !== "string" || value.length === 0) {
        throw new Error(`${label} must be a non-empty string`);
    }
    if (value.startsWith("-")) {
        throw new Error(`${label} must not start with '-' (looks like a git option): ${value}`);
    }
    if (!/^[A-Za-z0-9._/@^~+\-]+(\.{2,3}[A-Za-z0-9._/@^~+\-]+)?$/.test(value)) {
        throw new Error(`${label} contains characters not allowed in a git ref: ${value}`);
    }
}

async function detectRepoRoot(cwd) {
    try {
        return (await git(cwd, ["rev-parse", "--show-toplevel"])).trim();
    } catch {
        return cwd;
    }
}

async function detectBase(cwd) {
    // Prefer origin/HEAD (the default branch on the remote), then common names.
    try {
        const out = await git(cwd, ["symbolic-ref", "refs/remotes/origin/HEAD"]);
        return out.trim().replace(/^refs\/remotes\//, "");
    } catch {
        /* fall through */
    }
    for (const ref of ["origin/main", "origin/master", "main", "master"]) {
        try {
            await git(cwd, ["rev-parse", "--verify", ref]);
            return ref;
        } catch {
            /* try next */
        }
    }
    return "HEAD~1";
}

async function gatherDiff(cwd, base, head) {
    let mergeBase;
    try {
        mergeBase = (await git(cwd, ["merge-base", base, head])).trim();
    } catch {
        mergeBase = base;
    }
    // git diff --numstat outputs: <added>\t<removed>\t<path>
    // For binary files: "-\t-\tpath". Path may include "{old => new}" for renames.
    const out = await git(cwd, ["diff", "--numstat", `${mergeBase}...${head}`]);
    const files = [];
    for (const rawLine of out.split("\n")) {
        const line = rawLine.trimEnd();
        if (!line) continue;
        const tabIdx1 = line.indexOf("\t");
        const tabIdx2 = line.indexOf("\t", tabIdx1 + 1);
        if (tabIdx1 < 0 || tabIdx2 < 0) continue;
        const a = line.slice(0, tabIdx1);
        const r = line.slice(tabIdx1 + 1, tabIdx2);
        let file = line.slice(tabIdx2 + 1);
        // Normalize rename syntax: "a/{old => new}/b" -> "a/new/b", and "old => new" -> "new".
        file = file
            .replace(/\{[^{}]*=>\s*([^{}]+)\}/g, "$1")
            .replace(/^.+\s=>\s/, "");
        files.push({
            path: file,
            added: a === "-" ? 0 : parseInt(a, 10) || 0,
            removed: r === "-" ? 0 : parseInt(r, 10) || 0,
            binary: a === "-" && r === "-",
        });
    }
    return { mergeBase, files };
}

// ---------- categorization ----------

// Group files into a meaningful "project" bucket. Tuned for the Aspire repo
// layout but falls back to top-level directory for anything unknown.
function categorize(filePath) {
    const p = filePath.replace(/\\/g, "/");
    const parts = p.split("/");
    const top = parts[0];
    if (parts.length === 1) return "(repo root)";

    if (top === "src") {
        // src/Components/Aspire.X.Y/... -> "src/Components/Aspire.X.Y"
        if (parts[1] === "Components" && parts.length > 2) {
            return `src/Components/${parts[2]}`;
        }
        return `src/${parts[1]}`;
    }
    if (top === "tests") {
        return `tests/${parts[1]}`;
    }
    if (top === "playground") {
        return `playground/${parts[1]}`;
    }
    if (top === "tools") {
        return `tools/${parts[1]}`;
    }
    if (top === ".github") return ".github (CI / automation)";
    if (top === "eng") return "eng (build infrastructure)";
    if (top === "docs") return "docs";
    if (top === "extension") return "extension (VS Code)";
    if (top === ".agents") return ".agents (skills)";
    return top;
}

// ---------- characterization ----------

function characterize(files, addRatio, total) {
    const reGenerated = /(?:\.Designer\.cs|\.g\.cs|\.generated\.cs|\/api\/.*\.cs|\.xlf)$/i;
    const reLoc = /\.(resx|xlf)$/i;
    const reDocs = /\.(md|mdx|txt)$/i;
    const reCiYaml = /\.github\/.*\.ya?ml$/i;
    const reSnapshot = /\.(verified|received)\.[A-Za-z0-9]+$/i;
    const reTest = /(?:^|\/)tests?\//i;
    const reAssets = /\.(svg|png|jpg|jpeg|gif|webp|ico)$/i;

    const allOf = (pred) => files.length > 0 && files.every(pred);

    if (allOf((f) => reLoc.test(f.path))) {
        return { label: "Localization (mechanical)", tone: "mechanical" };
    }
    if (allOf((f) => reGenerated.test(f.path) || reLoc.test(f.path))) {
        return { label: "Generated files (mechanical)", tone: "mechanical" };
    }
    if (allOf((f) => reSnapshot.test(f.path))) {
        return { label: "Test snapshots (mechanical)", tone: "mechanical" };
    }
    if (allOf((f) => reAssets.test(f.path))) {
        return { label: "Assets (visual review)", tone: "mechanical" };
    }
    if (allOf((f) => reDocs.test(f.path))) {
        return { label: "Documentation (light review)", tone: "docs" };
    }
    if (allOf((f) => reCiYaml.test(f.path))) {
        return { label: "CI workflows (careful review)", tone: "careful" };
    }
    if (allOf((f) => reTest.test(f.path))) {
        return { label: "Tests (review for correctness & coverage)", tone: "detailed" };
    }

    if (total <= 10) {
        return { label: "Trivial change (quick scan)", tone: "mechanical" };
    }
    if (total <= 50) {
        if (addRatio > 0.85) return { label: "Small additions (quick review)", tone: "detailed" };
        if (addRatio < 0.15) return { label: "Small cleanup (quick review)", tone: "mechanical" };
        return { label: "Small edit (quick review)", tone: "detailed" };
    }
    if (total <= 250) {
        if (addRatio > 0.85) return { label: "Net-new code (detailed review)", tone: "detailed" };
        if (addRatio < 0.15) return { label: "Notable removal (detailed review)", tone: "detailed" };
        return { label: "Moderate edit (detailed review)", tone: "detailed" };
    }
    // > 250
    if (addRatio > 0.85) return { label: "Significant new code (careful review)", tone: "careful" };
    if (addRatio < 0.15) return { label: "Large removal (careful review)", tone: "careful" };
    return { label: "Significant refactor (careful review)", tone: "careful" };
}

// ---------- report build ----------

async function buildReport(opts) {
    const cwd = await detectRepoRoot(opts.cwd);
    if (opts.base !== undefined) assertSafeRef(opts.base, "base");
    if (opts.head !== undefined) assertSafeRef(opts.head, "head");
    const base = opts.base || (await detectBase(cwd));
    const head = opts.head || "HEAD";
    const branch = (await git(cwd, ["rev-parse", "--abbrev-ref", "HEAD"])).trim();
    const { mergeBase, files } = await gatherDiff(cwd, base, head);

    const groups = new Map();
    for (const f of files) {
        const cat = categorize(f.path);
        if (!groups.has(cat)) groups.set(cat, []);
        groups.get(cat).push(f);
    }

    const categories = [...groups.entries()].map(([name, fs]) => {
        const added = fs.reduce((s, f) => s + f.added, 0);
        const removed = fs.reduce((s, f) => s + f.removed, 0);
        // git's line-based diff has no real notion of "changed" lines — a modified
        // line shows up as 1 added + 1 removed. Approximate "changed" per file as
        // min(added, removed), then sum, which is a closer proxy than doing it
        // category-wide.
        const changed = fs.reduce((s, f) => s + Math.min(f.added, f.removed), 0);
        const total = added + removed;
        const addRatio = total > 0 ? added / total : 0;
        return {
            name,
            files: fs.length,
            added,
            removed,
            changed,
            total,
            characterization: characterize(fs, addRatio, total),
            filePaths: fs
                .slice()
                .sort((a, b) => b.added + b.removed - (a.added + a.removed))
                .map((f) => ({ path: f.path, added: f.added, removed: f.removed, binary: f.binary })),
        };
    });
    categories.sort((a, b) => b.total - a.total);

    const totals = {
        files: files.length,
        added: categories.reduce((s, c) => s + c.added, 0),
        removed: categories.reduce((s, c) => s + c.removed, 0),
        changed: categories.reduce((s, c) => s + c.changed, 0),
    };

    return {
        cwd,
        branch,
        base,
        head,
        mergeBase,
        generatedAt: new Date().toISOString(),
        totals,
        categories,
    };
}

// ---------- HTML ----------

function renderHtml() {
    // The shell fetches /data on load and on refresh; rendering happens client-side.
    return `<!doctype html>
<html>
<head>
<meta charset="utf-8" />
<title>LOC breakdown</title>
<style>
  :root {
    color-scheme: light dark;
    --fg: #1f2328;
    --muted: #57606a;
    --bg: #ffffff;
    --row: #f6f8fa;
    --border: #d0d7de;
    --added: #1a7f37;
    --removed: #cf222e;
    --changed: #9a6700;
    --mech: #6e7781;
    --detailed: #0969da;
    --careful: #bc4c00;
    --docs: #8250df;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --fg: #e6edf3;
      --muted: #8b949e;
      --bg: #0d1117;
      --row: #161b22;
      --border: #30363d;
      --added: #3fb950;
      --removed: #f85149;
      --changed: #d29922;
      --mech: #8b949e;
      --detailed: #58a6ff;
      --careful: #db6d28;
      --docs: #bc8cff;
    }
  }
  html, body { background: var(--bg); color: var(--fg); margin: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; padding: 1rem 1.25rem; }
  header { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; flex-wrap: wrap; }
  h1 { font-size: 1.1rem; margin: 0; }
  .meta { color: var(--muted); font-size: 0.8rem; }
  .meta code { background: var(--row); padding: 1px 5px; border-radius: 4px; }
  button { font: inherit; background: var(--row); color: var(--fg); border: 1px solid var(--border); border-radius: 6px; padding: 4px 10px; cursor: pointer; }
  button:hover { border-color: var(--fg); }
  .totals { margin: 0.75rem 0 1rem; color: var(--muted); font-size: 0.85rem; }
  .num { font-variant-numeric: tabular-nums; }
  .added { color: var(--added); }
  .removed { color: var(--removed); }
  .changed { color: var(--changed); }
  table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
  th { font-weight: 600; color: var(--muted); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.03em; }
  th.num, td.num { text-align: right; }
  tr.cat { cursor: pointer; }
  tr.cat:hover td { background: var(--row); }
  tr.files td { background: var(--row); padding: 0; }
  .files-inner { padding: 6px 10px 10px 28px; }
  .files-inner table { font-size: 0.8rem; }
  .files-inner th, .files-inner td { border: none; padding: 3px 8px; }
  .badge {
    display: inline-block; font-size: 0.72rem; padding: 2px 8px; border-radius: 999px;
    border: 1px solid currentColor; white-space: nowrap;
  }
  .badge.mechanical { color: var(--mech); }
  .badge.detailed   { color: var(--detailed); }
  .badge.careful    { color: var(--careful); }
  .badge.docs       { color: var(--docs); }
  .caret { display: inline-block; width: 0.8em; color: var(--muted); transition: transform 0.1s; }
  tr.cat.open .caret { transform: rotate(90deg); }
  .empty { color: var(--muted); padding: 2rem 0; text-align: center; }
  .error { color: var(--removed); padding: 1rem; background: var(--row); border-radius: 6px; white-space: pre-wrap; font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 0.8rem; }
</style>
</head>
<body>
<header>
  <div>
    <h1>Lines of code changed by category</h1>
    <div class="meta" id="meta">Loading…</div>
  </div>
  <button id="refresh" title="Re-run git diff">↻ Refresh</button>
</header>
<div class="totals" id="totals"></div>
<div id="content"><div class="empty">Loading…</div></div>

<script>
const fmt = new Intl.NumberFormat();

async function load() {
  const content = document.getElementById("content");
  const meta = document.getElementById("meta");
  const totalsEl = document.getElementById("totals");
  meta.textContent = "Loading…";
  content.innerHTML = '<div class="empty">Loading…</div>';
  totalsEl.textContent = "";
  try {
    const resp = await fetch("/data?ts=" + Date.now());
    if (!resp.ok) throw new Error("HTTP " + resp.status + ": " + await resp.text());
    const data = await resp.json();
    render(data);
  } catch (err) {
    meta.textContent = "";
    content.innerHTML = '<div class="error"></div>';
    content.querySelector(".error").textContent = String(err.stack || err.message || err);
  }
}

function render(data) {
  document.getElementById("meta").innerHTML =
    "Branch <code>" + escapeHtml(data.branch) + "</code> vs <code>" + escapeHtml(data.base) +
    "</code> (merge-base <code>" + data.mergeBase.slice(0, 8) + "</code>) — " +
    new Date(data.generatedAt).toLocaleTimeString();

  document.getElementById("totals").innerHTML =
    fmt.format(data.totals.files) + " files · " +
    '<span class="added num">+' + fmt.format(data.totals.added) + "</span> · " +
    '<span class="removed num">-' + fmt.format(data.totals.removed) + "</span> · " +
    '<span class="changed num">~' + fmt.format(data.totals.changed) + "</span> changed";

  if (!data.categories.length) {
    document.getElementById("content").innerHTML = '<div class="empty">No changes against base.</div>';
    return;
  }

  let html = '<table><thead><tr>' +
    '<th>Category</th>' +
    '<th class="num">Files</th>' +
    '<th class="num added">Added</th>' +
    '<th class="num removed">Removed</th>' +
    '<th class="num changed">Changed</th>' +
    '<th class="num">Total</th>' +
    '<th>Characterization</th>' +
    '</tr></thead><tbody>';

  for (let i = 0; i < data.categories.length; i++) {
    const c = data.categories[i];
    html += '<tr class="cat" data-idx="' + i + '">' +
      '<td><span class="caret">▸</span> ' + escapeHtml(c.name) + '</td>' +
      '<td class="num">' + fmt.format(c.files) + '</td>' +
      '<td class="num added">+' + fmt.format(c.added) + '</td>' +
      '<td class="num removed">-' + fmt.format(c.removed) + '</td>' +
      '<td class="num changed">~' + fmt.format(c.changed) + '</td>' +
      '<td class="num">' + fmt.format(c.total) + '</td>' +
      '<td><span class="badge ' + escapeHtml(c.characterization.tone) + '">' +
        escapeHtml(c.characterization.label) + '</span></td>' +
      '</tr>';
    html += '<tr class="files" data-for="' + i + '" hidden><td colspan="7"><div class="files-inner">' +
      renderFiles(c.filePaths) + '</div></td></tr>';
  }
  html += '</tbody></table>';
  document.getElementById("content").innerHTML = html;

  document.querySelectorAll("tr.cat").forEach((row) => {
    row.addEventListener("click", () => {
      const idx = row.getAttribute("data-idx");
      const detail = document.querySelector('tr.files[data-for="' + idx + '"]');
      const open = !detail.hasAttribute("hidden");
      if (open) {
        detail.setAttribute("hidden", "");
        row.classList.remove("open");
      } else {
        detail.removeAttribute("hidden");
        row.classList.add("open");
      }
    });
  });
}

function renderFiles(files) {
  let h = '<table><tbody>';
  for (const f of files) {
    h += '<tr>' +
      '<td>' + escapeHtml(f.path) + (f.binary ? ' <span class="meta">(binary)</span>' : '') + '</td>' +
      '<td class="num added">+' + fmt.format(f.added) + '</td>' +
      '<td class="num removed">-' + fmt.format(f.removed) + '</td>' +
      '</tr>';
  }
  return h + '</tbody></table>';
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}

document.getElementById("refresh").addEventListener("click", load);
load();
</script>
</body>
</html>`;
}

// ---------- server wiring ----------

async function startServer(opts) {
    const server = createServer(async (req, res) => {
        try {
            const url = new URL(req.url, "http://127.0.0.1");
            if (url.pathname === "/data") {
                const report = await buildReport(opts);
                res.setHeader("Content-Type", "application/json; charset=utf-8");
                res.setHeader("Cache-Control", "no-store");
                res.end(JSON.stringify(report));
                return;
            }
            res.setHeader("Content-Type", "text/html; charset=utf-8");
            res.setHeader("Cache-Control", "no-store");
            res.end(renderHtml());
        } catch (err) {
            res.statusCode = 500;
            res.setHeader("Content-Type", "text/plain; charset=utf-8");
            res.end(String(err && err.stack ? err.stack : err));
        }
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/` };
}

await joinSession({
    canvases: [
        createCanvas({
            id: "loc-breakdown",
            displayName: "LOC breakdown",
            description: "Lines of code changed grouped by project/category with review characterization.",
            inputSchema: {
                type: "object",
                properties: {
                    cwd: { type: "string", description: "Working directory inside the target git repo. Defaults to the active session's working directory (the user's worktree/repo); falls back to the extension process cwd only if the runtime did not supply one." },
                    base: { type: "string", description: "Base ref to diff against. Defaults to origin/HEAD." },
                    head: { type: "string", description: "Head ref. Defaults to HEAD." },
                },
            },
            actions: [
                {
                    name: "refresh",
                    description: "Recompute the LOC breakdown by re-running git diff and return the latest report as JSON.",
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        // Fall back to the session working directory (the user's worktree/repo) before
                        // process.cwd(), which for forked extensions is typically ~/.copilot and not a git repo.
                        const opts = entry ? entry.opts : { cwd: ctx.session?.workingDirectory || process.cwd() };
                        const report = await buildReport(opts);
                        return {
                            ok: true,
                            totals: report.totals,
                            categories: report.categories.map((c) => ({
                                name: c.name,
                                files: c.files,
                                added: c.added,
                                removed: c.removed,
                                changed: c.changed,
                                characterization: c.characterization.label,
                            })),
                        };
                    },
                },
            ],
            open: async (ctx) => {
                const input = ctx.input || {};
                // Prefer the explicit input.cwd, then the session's working directory supplied by
                // the runtime (CanvasSessionContext.workingDirectory). process.cwd() is a last resort
                // because the extension process cwd is not necessarily the target session's repo —
                // for forked extensions it's typically ~/.copilot, which is the wrong repo for this
                // report even on the rare setups where it happens to be a git repo itself.
                const opts = {
                    cwd: input.cwd || ctx.session?.workingDirectory || process.cwd(),
                    base: input.base,
                    head: input.head,
                };
                let entry = servers.get(ctx.instanceId);
                if (!entry) {
                    const started = await startServer(opts);
                    entry = { ...started, opts };
                    servers.set(ctx.instanceId, entry);
                }
                return { title: "LOC breakdown", url: entry.url };
            },
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(() => resolve()));
                }
            },
        }),
    ],
});

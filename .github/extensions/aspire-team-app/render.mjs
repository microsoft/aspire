// Renderer assets for the Aspire Team App canvas iframe.
//
// Served from the per-instance loopback server. Styling leans on the documented
// Copilot canvas theme tokens (with fallbacks that match the app's dark surface)
// so the dashboard reads as a first-party Copilot surface.
//
// UX model: this is a small surface, so there are no modals, overlays, or
// drawers. The shell is an in-canvas view router that transitions between full
// pages (queue / settings / accounts / notifications) with restrained motion.

export const HTML = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Aspire Team App</title>
    <link rel="stylesheet" href="styles.css" />
  </head>
  <body>
    <div id="app" class="app" aria-busy="true">
      <div class="topbar">
        <span class="brand"><span class="mark"><svg viewBox="0 0 32 32" fill="none" aria-hidden="true"><path d="M3.5 30C1.57 30 0 28.43 0 26.5C0 25.871 0.166 25.259 0.48 24.729L8.818 10.287L8.852 10.236L12.968 3.099C13.593 2.019 14.754 1.349 16 1.349C17.246 1.349 18.407 2.019 19.031 3.098L31.531 24.749C31.833 25.258 31.999 25.87 31.999 26.499C31.999 28.429 30.429 29.999 28.499 29.999L3.5 30Z" fill="#512BD4"/><path d="M25.33 18H16.99L16 16.28L13.13 11.31C13 11.09 12.82 10.9 12.58 10.77C11.87 10.35 10.95 10.6 10.53 11.32L14.7 4.10001C14.96 3.65001 15.44 3.35001 16 3.35001C16.56 3.35001 17.04 3.65001 17.3 4.10001L21.45 11.29L21.46 11.31L21.48 11.34L25.33 18Z" fill="#7455DD"/><path d="M30 26.5C30 27.33 29.33 28 28.5 28H20.17C21 28 21.67 27.33 21.67 26.5C21.67 26.23 21.59 25.97 21.47 25.75L17.3 18.53L16.99 18H25.33L29.8 25.75C29.93 25.97 30 26.23 30 26.5Z" fill="#9780E5"/><path d="M21.67 26.5C21.67 27.33 21 28 20.17 28H11.83C12.66 28 13.33 27.33 13.33 26.5C13.33 26.23 13.26 25.97 13.13 25.75C13.13 25.74 13.12 25.73 13.11 25.72L11.79 23.57L8.82004 18.72C8.55004 18.28 8.07004 18 7.54004 18H16.99L17.3 18.53L17.427 18.75L21.47 25.75C21.59 25.97 21.67 26.23 21.67 26.5Z" fill="#B9AAEE"/><path d="M13.33 26.5C13.33 27.33 12.66 28 11.83 28H3.5C2.67 28 2 27.33 2 26.5C2 26.23 2.07 25.97 2.2 25.75L6.24 18.75C6.51 18.29 7.01 18 7.54 18C8.07 18 8.55 18.28 8.82 18.72L11.79 23.57L13.11 25.72C13.12 25.73 13.13 25.74 13.13 25.75C13.26 25.97 13.33 26.23 13.33 26.5Z" fill="#DCD5F6"/><path d="M16.99 18H7.53999C7.00999 18 6.50999 18.29 6.23999 18.75L6.66999 18L10.49 11.39L10.53 11.33V11.32C10.95 10.6 11.87 10.35 12.58 10.77C12.82 10.9 13 11.09 13.13 11.31L16 16.28L16.99 18Z" fill="#9780E5"/></svg></span>Aspire Team App</span>
        <span class="sk sk-tabs"></span>
        <span class="spacer"></span>
        <span class="sk sk-chip"></span>
        <span class="sk sk-btn"></span>
        <span class="sk sk-btn"></span>
        <span class="sk sk-btn"></span>
      </div>
      <div class="subbar">
        <span class="sk sk-who"></span>
        <span class="sk sk-meta"></span>
        <span class="stats"><span class="sk sk-stat"></span><span class="sk sk-stat"></span><span class="sk sk-stat"></span></span>
      </div>
      <div class="lanes">
        <section class="lane">
          <div class="lane-head"><span class="sk sk-dot"></span><span class="sk sk-lane-title"></span></div>
          <div class="grid"><span class="sk sk-card"></span><span class="sk sk-card"></span><span class="sk sk-card"></span></div>
        </section>
        <section class="lane">
          <div class="lane-head"><span class="sk sk-dot"></span><span class="sk sk-lane-title"></span></div>
          <div class="grid"><span class="sk sk-card"></span><span class="sk sk-card"></span></div>
        </section>
      </div>
    </div>
    <script src="app.js"></script>
  </body>
</html>`;

export const STYLES = `
:root {
  --bg: var(--background-color-default, #0d1117);
  --surface: var(--n-1, #161b22);
  --surface-2: var(--n-2, #1c2128);
  --surface-3: var(--n-3, #262c36);
  --card: var(--n-2, #1e2530);
  --card-hover: var(--n-3, #252d39);
  --head-hover: color-mix(in srgb, var(--fg) 8%, transparent);
  --fg: var(--text-color-default, #e6edf3);
  --muted: var(--text-color-muted, #8b949e);
  --border: var(--border-color-default, #30363d);
  --border-soft: var(--n-2, #21262d);
  --border-strong: var(--border-color-muted, #484f58);
  --focus: var(--color-focus-outline, #4493f8);
  --white: var(--color-white, #fff);

  /* Brand purple - reserved for the brand mark, PR identity, and the loading accent */
  --accent: #a371f7;
  --accent-strong: #8957e5;
  --accent-2: #6e5cf0;
  --purple: #a371f7;

  /* Primary action green - matches the app's confirm button (sampled #347d39) */
  --green: #347d39;
  --green-emphasis: #3f9147;
  --green-border: transparent;
  --green-fg: #ffffff;

  /* Informational blue - links, identifiers, toggles, focus */
  --blue: var(--true-color-blue, #4493f8);

  --success: var(--true-color-green, #3fb950);
  --warning: var(--true-color-yellow, #d29922);
  --danger: var(--true-color-red, #f85149);

  --font: var(--font-sans, "Segoe UI", -apple-system, BlinkMacSystemFont, sans-serif);
  --mono: var(--font-mono, "Cascadia Code", "SFMono-Regular", Consolas, monospace);
  --radius: 6px;
}

* { box-sizing: border-box; }
html, body { margin: 0; height: 100%; }
body {
  background: var(--bg);
  color: var(--fg);
  font-family: var(--font);
  font-size: 13px;
  line-height: 1.5;
  -webkit-font-smoothing: antialiased;
}
a { color: inherit; text-decoration: none; }
button { font-family: inherit; cursor: pointer; }
:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; border-radius: 6px; }

.app { display: flex; flex-direction: column; min-height: 100%; position: relative; }

/* Top loading bar shown during refreshes (skeleton handles first load).
   A deliberate left-to-right paint-stroke fill, not a rushed shimmer. */
.app.loading::after {
  content: ""; position: fixed; left: 0; top: 0; height: 2px; width: 100%; z-index: 50;
  transform-origin: left center; transform: scaleX(0);
  background: linear-gradient(90deg, color-mix(in srgb, var(--accent-2) 65%, transparent), var(--accent) 72%, var(--accent-2));
  box-shadow: 0 0 8px color-mix(in srgb, var(--accent) 45%, transparent);
  animation: paintfill 2.6s cubic-bezier(.62, .03, .2, 1) infinite;
}
@keyframes paintfill {
  0%   { transform: scaleX(0);   opacity: .9; }
  70%  { transform: scaleX(.92); opacity: 1; }
  88%  { transform: scaleX(1);   opacity: 1; }
  100% { transform: scaleX(1);   opacity: 0; }
}

/* Header */
.topbar {
  position: sticky; top: 0; z-index: 20;
  display: flex; align-items: center; gap: 12px;
  padding: 10px 16px;
  background: color-mix(in srgb, var(--bg) 88%, transparent);
  backdrop-filter: blur(10px);
  border-bottom: 1px solid var(--border);
}
.brand { display: flex; align-items: center; gap: 9px; font-weight: 600; font-size: 14px; white-space: nowrap; }
button.brand { border: 0; background: transparent; padding: 2px 4px; margin: -2px -4px; border-radius: 7px; color: inherit; font-family: inherit; cursor: pointer; transition: background .15s; }
button.brand:hover { background: var(--surface); }
button.brand:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
.brand .mark {
  width: 22px; height: 22px; display: grid; place-items: center; flex: none;
}
.brand .mark svg { width: 22px; height: 22px; display: block; }
.tabs { display: flex; gap: 2px; margin-left: 6px; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 3px; }
.tab {
  border: 0; background: transparent; color: var(--muted);
  padding: 5px 12px; border-radius: 6px; font-size: 12.5px; font-weight: 500;
  transition: color .15s, background .15s;
}
.tab:hover { color: var(--fg); }
.tab.active { color: var(--fg); background: var(--surface-3); box-shadow: inset 0 0 0 1px var(--border); }
.spacer { flex: 1; }
.tb-actions { display: inline-flex; align-items: center; gap: 6px; }

.backbtn {
  display: inline-flex; align-items: center; gap: 6px; margin-left: 4px;
  border: 1px solid var(--border); background: var(--surface); color: var(--fg);
  height: 30px; padding: 0 11px 0 8px; border-radius: 6px; font-size: 12.5px; font-weight: 600;
  transition: border-color .15s, background .15s;
}
.backbtn:hover { border-color: var(--border-strong); background: var(--surface-2); }
.backbtn:active { transform: translateY(1px); }

.iconbtn {
  position: relative; border: 1px solid var(--border); background: var(--surface);
  color: var(--muted); width: 30px; height: 30px; border-radius: 6px;
  display: grid; place-items: center; transition: color .15s, border-color .15s, background .15s;
}
.iconbtn:hover { color: var(--fg); border-color: var(--border-strong); background: var(--surface-2); }
.iconbtn.active { color: var(--fg); border-color: var(--border-strong); background: var(--surface-3); box-shadow: inset 0 0 0 1px var(--border-soft); }
.iconbtn.spin svg { animation: spin 1s linear infinite; }
.badge {
  position: absolute; top: -6px; right: -6px; min-width: 16px; height: 16px; padding: 0 4px;
  border-radius: 999px; background: var(--danger); color: var(--white); font-size: 10px; font-weight: 700;
  display: grid; place-items: center; border: 2px solid var(--bg);
}
@keyframes spin { to { transform: rotate(360deg); } }

/* View router */
.viewport { flex: 1; }
.view { animation: viewEnter .22s ease both; }
.view.back { animation: viewEnterBack .22s ease both; }
@keyframes viewEnter { from { opacity: 0; transform: translateX(10px); } to { opacity: 1; transform: none; } }
@keyframes viewEnterBack { from { opacity: 0; transform: translateX(-10px); } to { opacity: 1; transform: none; } }

/* Sub-header (queue stats) */
.subbar {
  display: flex; align-items: center; flex-wrap: wrap; gap: 10px;
  padding: 12px 16px; border-bottom: 1px solid var(--border-soft);
}
.who { display: flex; align-items: center; gap: 8px; font-weight: 600; }
.who img { width: 22px; height: 22px; border-radius: 50%; border: 1px solid var(--border); }
.meta { color: var(--muted); font-size: 12px; }
.stats { display: flex; gap: 6px; margin-left: auto; flex-wrap: wrap; }
.stat {
  display: inline-flex; align-items: center; gap: 6px; padding: 4px 10px;
  border: 1px solid var(--border); border-radius: 999px; background: var(--surface); font-size: 12px;
}
.stat b { font-weight: 700; }
.dot { width: 7px; height: 7px; border-radius: 50%; display: inline-block; }
.t-success { color: var(--success); } .bg-success { background: var(--success); }
.t-warning { color: var(--warning); } .bg-warning { background: var(--warning); }
.t-danger  { color: var(--danger); }  .bg-danger  { background: var(--danger); }
.t-accent  { color: var(--purple); }  .bg-accent  { background: var(--purple); }
.t-muted   { color: var(--muted); }   .bg-muted   { background: var(--muted); }
.t-info    { color: var(--blue); }     .bg-info    { background: var(--blue); }

/* Lanes */
.lanes { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.lane-head {
  display: flex; align-items: center; gap: 8px; width: calc(100% + 16px);
  border: 0; background: transparent; color: var(--fg);
  padding: 5px 8px; margin: 0 -8px 1px; border-radius: 6px;
  transition: background .15s;
}
.lane-head:hover { background: var(--head-hover); }
.lane-ico { display: grid; place-items: center; width: 18px; height: 18px; flex: none; }
.lane-title { font-size: 13px; font-weight: 700; letter-spacing: .01em; }
.lane-count {
  display: inline-flex; align-items: center; line-height: 1; min-height: 20px;
  font-size: 11px; color: var(--muted); background: var(--surface);
  border: 1px solid var(--border); border-radius: 999px; padding: 0 8px; font-weight: 600;
}
.lane-detail { margin-left: auto; font-size: 11.5px; color: var(--muted); }
.lane-detail.capped {
  color: var(--accent); border: 1px solid color-mix(in srgb, var(--accent) 35%, transparent);
  border-radius: 999px; padding: 1px 7px; font-weight: 600; letter-spacing: .1px;
}
.meta-draft { color: var(--muted); opacity: .85; }
.lane-caret {
  order: -1; display: grid; place-items: center; color: var(--muted);
  width: 18px; height: 18px; flex: none; transition: transform .24s ease, color .15s;
}
.lane-head:hover .lane-caret { color: var(--fg); }
.lane.collapsed .lane-caret { transform: rotate(-90deg); }
.lane-body { display: grid; grid-template-rows: 1fr; transition: grid-template-rows .26s ease, opacity .2s ease; opacity: 1; }
.lane-body > .inner { overflow: hidden; min-height: 0; padding-top: 4px; }
.lane.collapsed .lane-body { grid-template-rows: 0fr; opacity: 0; }
/* Masonry card flow: cards pack into columns and use available vertical space
   instead of stretching to equal-height rows. Responsive via column-width. */
.grid { column-width: 280px; column-gap: 12px; }
/* When JS lays the queue out as real columns, .grid becomes a flex row of
   .mcol-col stacks so cards fill the left column first (predictable reading
   order) instead of multi-column height balancing. */
.grid.mcol { column-width: auto; display: flex; align-items: flex-start; gap: 12px; }
.mcol-col { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; }

/* Review board section dividers (For you / Needs attention / breakdown / Community). */
.board-sep {
  margin: 13px 2px 2px; padding-top: 12px; border-top: 1px solid var(--border);
  font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--muted);
  display: flex; align-items: center; gap: 8px;
}
.board-sep .sep-note { font-weight: 500; letter-spacing: 0; text-transform: none; color: var(--muted); opacity: .85; }
.lane-empty { color: var(--muted); font-size: 12.5px; padding: 10px 2px 2px; }

/* Generic collapsible body (shared by primary queues + secondary sections).
   Animates height via grid-template-rows so masonry grids keep their width. */
.collapse-body { display: grid; grid-template-rows: 1fr; transition: grid-template-rows .26s ease, opacity .2s ease; opacity: 1; }
.collapse-body > .collapse-inner { overflow: hidden; min-height: 0; }
.collapsible.collapsed > .collapse-body { grid-template-rows: 0fr; opacity: 0; }

/* Primary queue panels: For you / Needs attention / Your PRs outside.
   Collapsible sections that lead the review board. */
.qpanel { display: flex; flex-direction: column; }
.qhead {
  display: flex; align-items: center; gap: 8px; width: calc(100% + 16px);
  border: 0; background: transparent; color: var(--fg); cursor: pointer; font: inherit; text-align: left;
  padding: 5px 8px; margin: 0 -8px; border-radius: 6px; transition: background .15s;
}
.qhead:hover { background: var(--head-hover); }
.qdot { display: grid; place-items: center; width: 18px; height: 18px; flex: none; }
.qtitle { font-size: 13px; font-weight: 700; letter-spacing: .01em; }
.qmetric {
  margin-left: auto; display: inline-flex; align-items: center; line-height: 1; min-height: 20px;
  font-size: 11px; font-weight: 600; color: var(--muted);
  background: var(--surface); border: 1px solid var(--border); border-radius: 999px; padding: 0 9px;
}
.qmetric.capped { color: var(--accent); border-color: color-mix(in srgb, var(--accent) 35%, transparent); }
.qcaret { order: -1; display: grid; place-items: center; width: 18px; height: 18px; flex: none; color: var(--muted); opacity: .9; transition: transform .24s ease, color .15s, opacity .15s; }
.qhead:hover .qcaret { color: var(--fg); opacity: 1; }
.collapsible.collapsed .qcaret { transform: rotate(-90deg); }
.qsub { margin: 4px 0 10px; padding-left: 26px; color: var(--muted); font-size: 12px; line-height: 1.45; }
.qbody .grid { padding-top: 2px; }
.qbody .lane-empty { padding-left: 26px; }

/* Secondary reference groups (Community / Core team / Attention breakdown):
   collapsible, with a quieter header than the primary queues. */
.sect-group { padding-top: 11px; border-top: 1px solid var(--border); }
.sect-head {
  display: flex; align-items: center; gap: 8px; width: calc(100% + 16px);
  border: 0; background: transparent; color: inherit; cursor: pointer; font: inherit; text-align: left;
  padding: 4px 8px; margin: 0 -8px; border-radius: 6px; transition: background .15s;
}
.sect-head:hover { background: var(--head-hover); }
.sect-title { font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); }
.sect-icon { display: grid; place-items: center; width: 16px; height: 16px; flex: none; }
.sect-note { font-size: 11px; font-weight: 500; letter-spacing: 0; text-transform: none; color: var(--muted); opacity: .85; }
.sect-count {
  margin-left: auto; display: inline-flex; align-items: center; line-height: 1; min-height: 20px;
  font-size: 11px; color: var(--muted); background: var(--surface);
  border: 1px solid var(--border); border-radius: 999px; padding: 0 8px; font-weight: 600;
}
.sect-caret { order: -1; display: grid; place-items: center; width: 18px; height: 18px; flex: none; color: var(--muted); opacity: .9; transition: transform .24s ease, color .15s, opacity .15s; }
.sect-head:hover .sect-caret { color: var(--fg); opacity: 1; }
.collapsible.collapsed .sect-caret { transform: rotate(-90deg); }
.sect-count + .sect-caret { margin-left: 0; }
.sect-sub { margin: 5px 0 2px; padding-left: 0; color: var(--muted); font-size: 12px; line-height: 1.45; }
.dev-counts { display: grid; grid-template-columns: repeat(auto-fill, minmax(184px, 1fr)); gap: 6px; padding: 6px 0 2px; }
.dev-row {
  display: flex; align-items: center; gap: 10px; padding: 7px 10px;
  background: var(--card); border: 1px solid var(--border); border-radius: 8px;
}
.dev-main { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
.dev-av { width: 26px; height: 26px; border-radius: 50%; border: 1px solid var(--border); flex: none; background: var(--surface-3); }
.dev-name { font-size: 12.5px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dev-when { font-size: 10.5px; color: var(--muted); }
.dev-count {
  margin-left: auto; min-width: 22px; height: 20px; padding: 0 7px; flex: none;
  display: inline-flex; align-items: center; justify-content: center;
  background: var(--surface-3); border: 1px solid var(--border); border-radius: 999px;
  font-size: 11.5px; font-weight: 700; color: var(--fg);
}

.card {
  display: flex; flex-direction: column; gap: 8px; padding: 12px 14px;
  background: var(--card); border: 1px solid var(--border); border-radius: var(--radius);
  transition: border-color .15s, background .15s;
  break-inside: avoid; -webkit-column-break-inside: avoid; margin-bottom: 12px;
}
.card:hover { border-color: var(--border-strong); background: var(--card-hover); }
.card-top { display: flex; align-items: flex-start; gap: 8px; min-width: 0; }
.card-title { font-weight: 600; font-size: 13px; line-height: 1.35; color: var(--fg); min-width: 0; overflow-wrap: anywhere; word-break: break-word; }
.card-title:hover { color: var(--white); }
.card-sub { display: flex; align-items: center; gap: 8px; color: var(--muted); font-size: 11.5px; }
.card-sub .repo { font-family: var(--mono); }
.avatar { width: 18px; height: 18px; border-radius: 50%; border: 1px solid var(--border); }
.reason { color: var(--muted); font-size: 12px; }
.pills { display: flex; flex-wrap: wrap; gap: 5px; }
.pill {
  display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 500; line-height: 1;
  padding: 0 8px; min-height: 20px; border-radius: 999px; border: 1px solid transparent;
}
/* Label-badge tones mirror pr-dashboard's signal pills (translucent wash + crisp ~50% colored border
   + saturated text), sourced from our semantic theme tokens. accent = sky blue (pr-dashboard maps
   Docs/Bots/Draft/Review-started to blue; purple stays reserved for brand). muted stays theme-neutral. */
.pill.success { color: var(--success); background: color-mix(in srgb, var(--success) 14%, transparent); border-color: color-mix(in srgb, var(--success) 48%, transparent); }
.pill.warning { color: #f3d46b; background: color-mix(in srgb, var(--warning) 18%, transparent); border-color: color-mix(in srgb, var(--warning) 52%, transparent); }
.pill.danger  { color: var(--danger); background: color-mix(in srgb, var(--danger) 15%, transparent);  border-color: color-mix(in srgb, var(--danger) 48%, transparent); }
.pill.accent  { color: #7ab9ff; background: color-mix(in srgb, var(--blue) 14%, transparent);   border-color: color-mix(in srgb, var(--blue) 42%, transparent); }
.pill.info    { color: #7ab9ff; background: color-mix(in srgb, var(--blue) 14%, transparent);   border-color: color-mix(in srgb, var(--blue) 42%, transparent); }
.pill.muted   { color: var(--muted); background: color-mix(in srgb, var(--muted) 10%, transparent); border-color: color-mix(in srgb, var(--muted) 20%, transparent); }

/* Sub-page scaffold */
.page { padding: 16px; max-width: 760px; margin: 0 auto; }
.page-head { margin: 4px 2px 14px; }
.page-head h2 { margin: 0; font-size: 17px; font-weight: 700; }
.page-head p { margin: 4px 0 0; color: var(--muted); font-size: 12.5px; }
.page-actions { display: flex; align-items: center; gap: 8px; margin-left: auto; }

/* Settings form */
.section { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 14px 14px 12px; margin-bottom: 12px; }
.section h3 { margin: 0 0 3px; font-size: 13px; }
.section .hint { margin: 0 0 10px; color: var(--muted); font-size: 11.5px; }
.section .hint b { color: var(--fg); }
.policy { display: flex; flex-direction: column; gap: 8px; margin: 0 0 12px; }
.policy-row {
  display: flex; gap: 9px; align-items: flex-start;
  background: var(--surface-3, color-mix(in srgb, var(--accent) 9%, transparent));
  border: 1px solid var(--border); border-radius: 8px; padding: 9px 11px;
  font-size: 11.5px; line-height: 1.5; color: var(--muted);
}
.policy-row svg { flex: none; width: 15px; height: 15px; margin-top: 1px; color: var(--accent); }
.policy-row b { color: var(--fg); font-weight: 600; }
.field label { display: block; font-size: 12px; font-weight: 600; margin-bottom: 5px; }
.field + .field { margin-top: 12px; }
.field textarea {
  width: 100%; min-height: 84px; resize: vertical; background: var(--bg); color: var(--fg);
  border: 1px solid var(--border); border-radius: 8px; padding: 9px; font-family: var(--mono); font-size: 11.5px; line-height: 1.6;
}
.field input[type=text] {
  width: 100%; background: var(--bg); color: var(--fg);
  border: 1px solid var(--border); border-radius: 6px; padding: 8px 9px; font-size: 12.5px;
}
.field textarea:focus, .field input[type=text]:focus { outline: 2px solid var(--focus); outline-offset: 1px; border-color: var(--focus); }

/* Watched-repository editor: add-input + plus, then a read-only list with inline edit/delete */
.repo-add { display: flex; gap: 8px; align-items: stretch; }
.repo-add input {
  flex: 1; height: 32px; background: var(--bg); color: var(--fg); border: 1px solid var(--border);
  border-radius: 6px; padding: 0 11px; font-size: 12.5px; font-family: var(--mono);
  transition: border-color .15s ease, box-shadow .15s ease;
}
.repo-add input::placeholder { color: var(--muted); font-family: var(--font); }
.repo-add input:focus { outline: 2px solid var(--focus); outline-offset: 1px; border-color: var(--focus); }
.repo-add-btn {
  flex: none; width: 32px; height: 32px; border-radius: 6px; border: 1px solid var(--green-border); cursor: pointer;
  background: var(--green); color: var(--white); display: grid; place-items: center;
  transition: background .15s ease, transform .1s ease;
}
.repo-add-btn:hover { background: var(--green-emphasis); }
.repo-add-btn:active { transform: translateY(1px); }
.repo-err {
  font-size: 11.5px; color: var(--danger); max-height: 0; opacity: 0; overflow: hidden;
  transition: max-height .2s ease, opacity .2s ease, margin-top .2s ease;
}
.repo-err.show { max-height: 22px; opacity: 1; margin-top: 8px; }
.repo-list { list-style: none; margin: 12px 0 0; padding: 0; }
.repo-empty { color: var(--muted); font-size: 12px; padding: 10px 2px 2px; }
.repo-row {
  display: flex; align-items: center; gap: 10px; min-height: 40px; padding: 6px 6px 6px 10px;
  border-top: 1px solid var(--border); border-radius: 8px; overflow: hidden;
  transition: background .15s ease;
}
.repo-row:first-child { border-top-color: transparent; }
.repo-row:hover { background: var(--card-hover); }
.repo-row:hover, .repo-row:hover + .repo-row { border-top-color: transparent; }
.repo-name { flex: 1; font-family: var(--mono); font-size: 12px; color: var(--fg); word-break: break-all; }
.repo-acts { display: inline-flex; gap: 2px; opacity: 0; transition: opacity .15s ease; }
.repo-row:hover .repo-acts, .repo-row.editing .repo-acts { opacity: 1; }
.repo-ico {
  width: 28px; height: 28px; border-radius: 6px; border: 1px solid transparent; background: transparent;
  color: var(--muted); display: grid; place-items: center; cursor: pointer;
  transition: background .15s ease, color .15s ease, border-color .15s ease;
}
.repo-ico:hover { background: var(--surface-2); color: var(--fg); border-color: var(--border); }
.repo-ico.danger:hover { color: var(--danger); border-color: color-mix(in srgb, var(--danger) 45%, transparent); }
.repo-ico.ok:hover { color: var(--success); border-color: color-mix(in srgb, var(--success) 45%, transparent); }
.repo-edit-input {
  flex: 1; background: var(--bg); color: var(--fg); border: 1px solid var(--focus); border-radius: 6px;
  padding: 7px 9px; font-family: var(--mono); font-size: 12px; outline: 2px solid var(--focus); outline-offset: 0;
}
.repo-row.added { animation: repoIn .26s cubic-bezier(.2,.7,.2,1); }
.repo-row.removing { animation: repoOut .18s ease forwards; pointer-events: none; }
@keyframes repoIn { from { opacity: 0; transform: translateY(-5px); } to { opacity: 1; transform: none; } }
@keyframes repoOut { from { opacity: 1; max-height: 60px; } to { opacity: 0; max-height: 0; min-height: 0; padding-top: 0; padding-bottom: 0; transform: translateX(10px); } }
.repo-add input.shake, .repo-edit-input.shake { animation: shake .3s ease; border-color: var(--danger); }
@keyframes shake { 0%,100% { transform: translateX(0); } 20% { transform: translateX(-5px); } 40% { transform: translateX(5px); } 60% { transform: translateX(-3px); } 80% { transform: translateX(3px); } }
.toggle-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 9px 0; border-top: 1px solid var(--border); }
.toggle-row:first-of-type { border-top: 0; }
.toggle-row .tl { display: block; font-size: 12.5px; font-weight: 600; }
.toggle-row .td { display: block; font-size: 11.5px; color: var(--muted); margin-top: 1px; }
.switch { position: relative; width: 36px; height: 20px; flex: none; }
.switch input { opacity: 0; width: 0; height: 0; }
.slider { position: absolute; inset: 0; background: var(--border); border-radius: 999px; transition: .15s; }
.slider::before { content: ""; position: absolute; height: 14px; width: 14px; left: 3px; top: 3px; background: var(--white); border-radius: 50%; transition: .15s; }
.switch input:checked + .slider { background: var(--blue); }
.switch input:checked + .slider::before { transform: translateX(16px); }
.row-actions { display: flex; gap: 8px; justify-content: flex-end; align-items: center; }
.btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 8px;
  height: 30px; padding: 0 12px; border: 1px solid var(--green-border); border-radius: 8px;
  color: var(--white); font-weight: 600; font-size: 12.5px; line-height: 1; background: var(--green); white-space: nowrap;
  transition: background .15s, transform .1s, border-color .15s, box-shadow .15s;
}
.btn:hover { background: var(--green-emphasis); }
.btn:active { transform: translateY(1px); }
.btn:focus-visible { outline: 2px solid var(--color-focus-outline, var(--blue)); outline-offset: 2px; }
.btn.ghost { background: var(--surface-3); border-color: var(--border-strong); color: var(--fg); }
.btn.ghost:hover { border-color: var(--border-strong); background: var(--card-hover); }
.btn.block { width: 100%; justify-content: center; }
/* Keyboard-hint chips, mirroring the app's Cancel/Continue affordances. */
.btn kbd {
  display: inline-flex; align-items: center; justify-content: center; min-width: 17px; height: 17px;
  padding: 0 4px; border-radius: 5px; font-family: var(--font-sans, inherit);
  font-size: 10.5px; font-weight: 600; line-height: 1; letter-spacing: .2px;
  border: 1px solid rgba(0,0,0,.16); background: rgba(0,0,0,.22); color: rgba(255,255,255,.95);
}
.btn.ghost kbd { border-color: var(--border-strong); background: var(--surface-3); color: var(--muted); }

/* Accounts */
.acct-intro { color: var(--muted); font-size: 12px; margin: 0 2px 12px; }
.acct-list { display: flex; flex-direction: column; gap: 10px; }
.acct-card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; transition: border-color .15s, box-shadow .15s; }
.acct-card:hover { border-color: var(--border-strong); }
.acct-card.active { border-color: color-mix(in srgb, var(--green) 60%, var(--border)); box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--green) 30%, transparent); }
.acct-head { display: flex; align-items: center; }
.acct-main {
  display: flex; align-items: center; gap: 12px; flex: 1; min-width: 0; text-align: left;
  padding: 12px 13px; background: transparent; color: var(--fg); border: 0;
}
.acct-main:disabled { opacity: .6; cursor: not-allowed; }
.acct-main > img { width: 36px; height: 36px; border-radius: 50%; border: 1px solid var(--border); flex: none; }
.acct-id { min-width: 0; flex: 1; }
.acct-name { font-size: 13.5px; font-weight: 700; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.acct-status { font-size: 11.5px; display: inline-flex; align-items: center; gap: 6px; }
.src-badges { display: inline-flex; gap: 5px; flex-wrap: wrap; }
.src-badge {
  font-size: 10px; font-weight: 700; letter-spacing: .02em; color: var(--muted);
  border: 1px solid var(--border); border-radius: 6px; padding: 1px 7px; background: var(--surface-3);
}
.acct-name .count { font-size: 10px; color: var(--muted); font-weight: 600; }
.acct-meta { font-size: 11.5px; color: var(--muted); margin-top: 3px; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.acct-meta .scopes { font-family: var(--mono); font-size: 10.5px; }
.acct-right { display: flex; align-items: center; gap: 12px; flex: none; padding-right: 13px; }
.use-tag { font-size: 11px; font-weight: 600; color: var(--success); display: inline-flex; align-items: center; gap: 5px; }
.caret {
  border: 0; background: transparent; color: var(--muted);
  width: 22px; height: 28px; border-radius: 6px; display: grid; place-items: center; flex: none;
  margin: 0; align-self: center; cursor: pointer;
  transition: transform .26s ease, color .15s;
}
.caret svg { width: 16px; height: 16px; }
.caret:hover { color: var(--fg); }
.acct-card.open .caret { transform: rotate(180deg); }

/* Expanding control */
.acct-detail { display: grid; grid-template-rows: 0fr; transition: grid-template-rows .26s ease, opacity .2s ease; opacity: 0; }
.acct-detail > .inner { overflow: hidden; }
.acct-card.open .acct-detail { grid-template-rows: 1fr; opacity: 1; }
.src-row { display: flex; align-items: center; gap: 10px; padding: 8px 13px; border-top: 1px solid var(--border); flex-wrap: wrap; }
.src-row .sname { font-size: 12px; font-weight: 600; display: inline-flex; align-items: center; gap: 7px; }
.src-row .schip { font-size: 9.5px; font-weight: 600; color: var(--muted); border: 1px solid var(--border); border-radius: 5px; padding: 0 5px; }
.src-row .smeta { margin-left: auto; font-size: 11px; color: var(--muted); display: inline-flex; align-items: center; gap: 10px; flex-wrap: wrap; justify-content: flex-end; }
.src-row .smeta .scopes { font-family: var(--mono); font-size: 10.5px; }

.rescan-btn { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--border); background: var(--surface); color: var(--fg); border-radius: 6px; height: 30px; padding: 0 11px; font-size: 12.5px; font-weight: 600; }
.rescan-btn:hover { color: var(--fg); border-color: var(--border-strong); background: var(--surface-2); }
.rescan-btn:active { transform: translateY(1px); }
.rescan-btn.spin svg { animation: spin 1s linear infinite; }

/* Notifications */
.notif-list { display: flex; flex-direction: column; gap: 8px; }
.notif-card {
  display: flex; align-items: flex-start; gap: 10px; padding: 11px 12px;
  background: var(--card); border: 1px solid var(--border); border-radius: 8px;
  transition: border-color .15s, opacity .22s ease, transform .22s ease, margin .22s ease, max-height .22s ease, padding .22s ease;
  max-height: 120px; overflow: hidden;
}
.notif-card:hover { border-color: var(--border-strong); background: var(--card-hover); }
.notif-card.removing { opacity: 0; transform: translateX(14px); max-height: 0; padding-top: 0; padding-bottom: 0; margin-top: -8px; }
.notif-card .ndot { margin-top: 5px; width: 8px; height: 8px; border-radius: 50%; flex: none; }
.notif-card .nbody { min-width: 0; flex: 1; }
.notif-card .ntitle { display: block; font-size: 13px; font-weight: 600; overflow-wrap: anywhere; word-break: break-word; }
.notif-card .ndetail { display: block; font-size: 11.5px; color: var(--muted); margin-top: 2px; }
.notif-card .ndetail .repo { font-family: var(--mono); }
.notif-foot { margin-top: 14px; padding-top: 12px; border-top: 1px solid var(--border); display: flex; }
.linklike {
  display: inline-flex; align-items: center; gap: 7px; border: 0; background: transparent; cursor: pointer;
  color: var(--blue); font-family: inherit; font-size: 12.5px; font-weight: 500; padding: 4px 6px; margin: -4px -6px; border-radius: 6px;
  transition: background .15s, color .15s;
}
.linklike:hover { background: var(--surface); color: var(--fg); }
.linklike svg { width: 14px; height: 14px; }
.linklike:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
.dismiss {
  border: 1px solid transparent; background: transparent; color: var(--muted);
  width: 26px; height: 26px; border-radius: 6px; display: grid; place-items: center; flex: none;
  transition: color .15s, background .15s, border-color .15s;
}
.dismiss:hover { color: var(--fg); background: var(--surface-2); border-color: var(--border); }

/* Account chip */
.acct-chip {
  display: inline-flex; align-items: center; gap: 7px; height: 30px; padding: 0 9px;
  border: 1px solid var(--border); background: var(--surface); border-radius: 6px;
  color: var(--fg); font-size: 12px; font-weight: 600; max-width: 190px;
  transition: border-color .15s, background .15s;
}
.acct-chip:hover { border-color: var(--border-strong); background: var(--surface-2); }
.acct-chip.active { border-color: var(--border-strong); background: var(--surface-3); color: var(--fg); }
.acct-chip img { width: 19px; height: 19px; border-radius: 50%; border: 1px solid var(--border); flex: none; }
.acct-chip .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.acct-chip .sdot { width: 7px; height: 7px; border-radius: 50%; flex: none; }

/* Stacked / overlapping avatars for multiple active accounts */
.stack { display: inline-flex; align-items: center; }
.stk-av { position: relative; display: inline-grid; place-items: center; margin-left: -7px; }
.stk-av:first-child { margin-left: 0; }
.stk-av .acct-av { width: 22px; height: 22px; border-radius: 50%; border: 2px solid var(--bg); display: block; background: var(--surface); object-fit: cover; }
.stk-more { margin-left: 6px; font-size: 11px; color: var(--muted); font-weight: 600; }
.ent-dot { position: absolute; right: -3px; bottom: -3px; width: 13px; height: 13px; border-radius: 50%; background: var(--blue); color: var(--white); display: grid; place-items: center; border: 2px solid var(--bg); }
.ent-dot svg { width: 8px; height: 8px; }
.acct-chip .stk-av .acct-av { width: 19px; height: 19px; }
.who .stk-av .acct-av { width: 24px; height: 24px; }
.who-name { font-weight: 600; }
.who-more { color: var(--muted); font-weight: 500; font-size: 12px; }

/* Enterprise badge - makes a non-github.com account obvious */
.ent-badge {
  display: inline-flex; align-items: center; gap: 4px; font-size: 10.5px; font-weight: 700; letter-spacing: .02em;
  color: var(--blue); background: color-mix(in srgb, var(--blue) 14%, transparent);
  border: 1px solid color-mix(in srgb, var(--blue) 34%, transparent); border-radius: 999px; padding: 1px 8px 1px 6px; white-space: nowrap;
}
.ent-badge svg { width: 11px; height: 11px; }
.ent-badge.sm { padding: 2px 5px; }
.acct-chip .ent-badge { height: 18px; }
.schip.ent { color: var(--blue); border-color: color-mix(in srgb, var(--blue) 40%, transparent); }

/* Per-account watched-repository editor (inside the account detail) */
.acct-repos { padding: 12px 13px 8px; border-top: 1px solid var(--border); }
.acct-repos-head { display: flex; align-items: center; gap: 8px; font-size: 12px; font-weight: 700; }
.acct-repos-head .rcount { font-size: 10px; font-weight: 600; color: var(--muted); background: var(--surface-3); border: 1px solid var(--border); border-radius: 999px; padding: 0 7px; }
.acct-repos-hint { margin: 3px 0 10px; color: var(--muted); font-size: 11px; }
.acct-repos-hint code { font-family: var(--mono); }
.src-list { border-top: 1px solid var(--border); }

/* Composed states */
.state { padding: 48px 20px; text-align: center; color: var(--muted); max-width: 460px; margin: 0 auto; }
.state .ico {
  width: 54px; height: 54px; border-radius: 14px; margin: 0 auto 14px; display: grid; place-items: center;
  background: var(--surface); border: 1px solid var(--border); color: var(--muted);
}
.state h2 { color: var(--fg); font-size: 17px; margin: 0 0 6px; }
.state p { margin: 0 auto; font-size: 13px; line-height: 1.55; }
.state .cmd { display: inline-block; margin-top: 14px; font-family: var(--mono); font-size: 12px; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 7px 11px; color: var(--fg); }
.state .state-cta { margin-top: 16px; display: flex; gap: 8px; justify-content: center; }
.errbar { margin: 12px 16px 0; padding: 9px 12px; border-radius: 8px; background: color-mix(in srgb, var(--danger) 12%, transparent); border: 1px solid color-mix(in srgb, var(--danger) 35%, transparent); color: var(--danger); font-size: 11.5px; }
.errbar.loaderr { display: flex; align-items: center; gap: 8px; }
.errbar-x { margin-left: auto; display: inline-flex; align-items: center; justify-content: center; padding: 2px; border: 0; border-radius: 6px; background: transparent; color: inherit; cursor: pointer; opacity: .75; }
.errbar-x:hover { opacity: 1; background: color-mix(in srgb, var(--danger) 18%, transparent); }
.errbar-x svg { width: 13px; height: 13px; }
.empty-lane { color: var(--muted); font-size: 12px; padding: 6px 0; }

/* Skeleton shimmer */
.sk { position: relative; display: inline-block; border-radius: 8px; background: var(--surface); overflow: hidden; }
.sk::after { content: ""; position: absolute; inset: 0; transform: translateX(-100%);
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--fg) 8%, transparent), transparent);
  animation: shimmer 1.3s infinite; }
@keyframes shimmer { 100% { transform: translateX(100%); } }
.sk-tabs { width: 168px; height: 30px; margin-left: 6px; }
.sk-chip { width: 120px; height: 30px; }
.sk-btn { width: 30px; height: 30px; }
.sk-who { width: 130px; height: 22px; }
.sk-meta { width: 220px; height: 14px; }
.sk-stat { width: 96px; height: 26px; border-radius: 999px; }
.sk-dot { width: 10px; height: 10px; border-radius: 50%; }
.sk-lane-title { width: 150px; height: 16px; }
.sk-card { height: 96px; border-radius: var(--radius); display: block; width: 100%; }

@media (prefers-reduced-motion: reduce) {
  .view, .view.back { animation: none; }
  .app.loading::after { animation: none; transform: scaleX(1); opacity: .55; }
  .sk::after { animation: none; }
  .iconbtn.spin svg, .rescan-btn.spin svg { animation: none; }
  .caret, .acct-detail, .notif-card, .card, .lane-body, .lane-caret, .repo-row, .repo-acts, .repo-err, .repo-ico { transition: none; }
  .repo-row.added, .repo-row.removing, .repo-add input.shake, .repo-edit-input.shake { animation: none; }
}

@media (max-width: 600px) {
  .topbar { gap: 8px; padding: 10px 13px; }
  .brand-text { display: none; }
  .tabs { margin-left: 0; }
  .tab { padding: 5px 11px; }
  .acct-chip { max-width: 150px; }
  .acct-chip .name { max-width: 70px; }
  .page { padding: 14px; }
}
@media (max-width: 470px) {
  .topbar { gap: 6px; padding: 10px 10px; }
  .tab { padding: 5px 9px; font-size: 12px; }
  .acct-chip { padding: 0 7px; gap: 6px; }
  .acct-chip .name { display: none; }
  .iconbtn { width: 28px; height: 28px; }
  .backbtn { height: 28px; }
}
`;

export const APP_JS = String.raw`
const app = document.getElementById("app");
let state = null;
let prefs = null;
let view = "queue";       // queue | settings | accounts | notifications
let keysBound = false;
let prevRank = 0;
let refreshing = false;
let rescanning = false;
let loadError = null;
const expanded = new Set(); // account ids whose detail (sources + repos) is expanded
const collapsedLanes = new Set(); // lane ids the user collapsed (survives re-render + SSE)
const draftReposByAcct = {}; // account id -> working copy of that account's watched repos
const editingByAcct = {};    // account id -> index of the repo row being inline-edited, or -1

const RANK = { queue: 0, notifications: 1, accounts: 1, settings: 1 };

const ICONS = {
  refresh: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/></svg>',
  gear: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>',
  bell: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>',
  bellBig: '<svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>',
  chev: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9l6 6 6-6"/></svg>',
  back: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 12H5"/><path d="M12 19l-7-7 7-7"/></svg>',
  check: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6L9 17l-5-5"/></svg>',
  x: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6L6 18"/><path d="M6 6l12 12"/></svg>',
  plus: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>',
  pencil: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
  trash: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>',
  users: '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>',
  alert: '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
  eye: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>',
  merge: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M6 21V9a9 9 0 0 0 9 9"/></svg>',
  xcircle: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M15 9l-6 6"/><path d="M9 9l6 6"/></svg>',
  chat: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
  pr: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M13 6h3a2 2 0 0 1 2 2v7"/><line x1="6" y1="9" x2="6" y2="21"/></svg>',
  tag: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>',
  clock: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
  dot2: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="12" r="4"/></svg>',
  building: '<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="2" width="16" height="20" rx="2"/><path d="M9 22v-4h6v4"/><path d="M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01"/></svg>',
  sparkle: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 3l1.9 5.1L18 10l-5.1 1.9L11 17l-1.9-5.1L4 10l5.1-1.9z"/><path d="M19 14l.7 1.9 1.9.7-1.9.7-.7 1.9-.7-1.9-1.9-.7 1.9-.7z"/></svg>',
  alertSm: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
  globe: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>',
  usersSm: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>',
  layers: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>',
};

const LOGO = '<svg viewBox="0 0 32 32" fill="none" aria-hidden="true"><path d="M3.5 30C1.57 30 0 28.43 0 26.5C0 25.871 0.166 25.259 0.48 24.729L8.818 10.287L8.852 10.236L12.968 3.099C13.593 2.019 14.754 1.349 16 1.349C17.246 1.349 18.407 2.019 19.031 3.098L31.531 24.749C31.833 25.258 31.999 25.87 31.999 26.499C31.999 28.429 30.429 29.999 28.499 29.999L3.5 30Z" fill="#512BD4"/><path d="M25.33 18H16.99L16 16.28L13.13 11.31C13 11.09 12.82 10.9 12.58 10.77C11.87 10.35 10.95 10.6 10.53 11.32L14.7 4.10001C14.96 3.65001 15.44 3.35001 16 3.35001C16.56 3.35001 17.04 3.65001 17.3 4.10001L21.45 11.29L21.46 11.31L21.48 11.34L25.33 18Z" fill="#7455DD"/><path d="M30 26.5C30 27.33 29.33 28 28.5 28H20.17C21 28 21.67 27.33 21.67 26.5C21.67 26.23 21.59 25.97 21.47 25.75L17.3 18.53L16.99 18H25.33L29.8 25.75C29.93 25.97 30 26.23 30 26.5Z" fill="#9780E5"/><path d="M21.67 26.5C21.67 27.33 21 28 20.17 28H11.83C12.66 28 13.33 27.33 13.33 26.5C13.33 26.23 13.26 25.97 13.13 25.75C13.13 25.74 13.12 25.73 13.11 25.72L11.79 23.57L8.82004 18.72C8.55004 18.28 8.07004 18 7.54004 18H16.99L17.3 18.53L17.427 18.75L21.47 25.75C21.59 25.97 21.67 26.23 21.67 26.5Z" fill="#B9AAEE"/><path d="M13.33 26.5C13.33 27.33 12.66 28 11.83 28H3.5C2.67 28 2 27.33 2 26.5C2 26.23 2.07 25.97 2.2 25.75L6.24 18.75C6.51 18.29 7.01 18 7.54 18C8.07 18 8.55 18.28 8.82 18.72L11.79 23.57L13.11 25.72C13.12 25.73 13.13 25.74 13.13 25.75C13.26 25.97 13.33 26.23 13.33 26.5Z" fill="#DCD5F6"/><path d="M16.99 18H7.53999C7.00999 18 6.50999 18.29 6.23999 18.75L6.66999 18L10.49 11.39L10.53 11.33V11.32C10.95 10.6 11.87 10.35 12.58 10.77C12.82 10.9 13 11.09 13.13 11.31L16 16.28L16.99 18Z" fill="#9780E5"/></svg>';

const ACCT_STATUS = {
  ok: { tone: "success", label: "Full access" },
  partial: { tone: "warning", label: "Partial access" },
  limited: { tone: "warning", label: "No repo access" },
  failed: { tone: "danger", label: "Sign-in failed" },
};
const SRC_LABEL = { gh: "GitHub CLI", env: "Environment", copilot: "Copilot" };
function acctTone(a) { return (ACCT_STATUS[a && a.status] || ACCT_STATUS.failed).tone; }
function srcLabel(s) { return SRC_LABEL[s] || s; }

function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function timeAgo(iso) {
  const s = Math.floor((Date.now() - new Date(iso)) / 1000);
  if (s < 60) return s + "s ago";
  const m = Math.floor(s / 60); if (m < 60) return m + "m ago";
  const h = Math.floor(m / 60); if (h < 24) return h + "h ago";
  return Math.floor(h / 24) + "d ago";
}
function shortRepo(r) { const p = String(r).split("/"); return p[p.length - 1]; }
function cssEsc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/[^\w-]/g, "\\$&"); }

// Neutral gray silhouette used when an avatar URL 404s (renamed users, bots,
// enterprise hosts the browser can't reach). Base64 keeps it safe inside both the
// double-quoted attribute and the single-quoted onerror JS string.
const FALLBACK_AVATAR = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MCIgaGVpZ2h0PSI0MCIgdmlld0JveD0iMCAwIDQwIDQwIj48cmVjdCB3aWR0aD0iNDAiIGhlaWdodD0iNDAiIHJ4PSIyMCIgZmlsbD0iIzMwMzYzZCIvPjxjaXJjbGUgY3g9IjIwIiBjeT0iMTUuNSIgcj0iNi41IiBmaWxsPSIjOGI5NDllIi8+PHBhdGggZD0iTTguNSAzMy41YzAtNi40IDUuMi0xMC41IDExLjUtMTAuNXMxMS41IDQuMSAxMS41IDEwLjV6IiBmaWxsPSIjOGI5NDllIi8+PC9zdmc+";

// Build an <img> for an avatar, preferring the real avatarUrl from the API and
// degrading to github.com/<login>.png, then to the inline fallback on error.
function avatarTag(url, login, cls, size) {
  const px = size || 40;
  const src = url || ("https://github.com/" + encodeURIComponent(login || "github") + ".png?size=" + px);
  return '<img class="' + (cls || "avatar") + '" src="' + esc(src) + '" alt="" referrerpolicy="no-referrer" loading="lazy" ' +
    'onerror="this.onerror=null;this.src=\'' + FALLBACK_AVATAR + '\'" />';
}
function acctAvatar(a, size) {
  const url = a && a.avatarUrl ? a.avatarUrl : null;
  const login = a && a.login ? a.login : (typeof a === "string" ? a : "github");
  return avatarTag(url, login, "acct-av", size);
}

/* ---- data ---- */

// Parse a JSON response, surfacing the server's { error } message on any non-2xx
// (origin-guard 403, 500, etc.) instead of treating the error body as a successful
// payload. The loopback server always replies with JSON, but tolerate a missing or
// unparseable body so a raw failure still yields a useful message.
async function readJson(res) {
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new Error((data && data.error) || ("Request failed (" + res.status + ")"));
  return data;
}

async function load() {
  try {
    const res = await fetch("api/state");
    const data = await readJson(res);
    state = data.dashboard; prefs = data.prefs; loadError = null;
  } catch (e) {
    loadError = String((e && e.message) || e);
  }
  render();
}

async function withRefresh(fn) {
  refreshing = true; setLoading(true);
  try {
    const data = await fn();
    if (data && data.dashboard) { state = data.dashboard; prefs = data.prefs; loadError = null; }
  } catch (e) {
    loadError = String((e && e.message) || e);
  } finally {
    refreshing = false; render();
  }
}

function setLoading(on) {
  app.classList.toggle("loading", on);
  const rb = document.getElementById("refresh-btn");
  if (rb) rb.classList.toggle("spin", on);
}

async function postJSON(path, body) {
  const res = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });
  return readJson(res);
}

const refresh = () => withRefresh(() => postJSON("api/refresh"));
const setMode = (mode) => { if (state && state.mode === mode) return; goView("queue", false); return withRefresh(() => postJSON("api/mode", { mode })); };
const toggleAccountActive = (id, active) => withRefresh(() => postJSON("api/account/toggle", { id, active }));

// Persist one account's repos without a full refresh/broadcast (the editor owns
// the DOM and a re-render would interrupt typing). Fire-and-forget; the in-memory
// copies are already updated by the editor.
function persistAccountRepos(id) {
  const repos = (draftReposByAcct[id] || []).slice();
  postJSON("api/account/repos", { id, repos }).then((data) => {
    if (data && data.dashboard) { state = data.dashboard; prefs = data.prefs; }
    repoErr(id, "");
  }).catch((e) => {
    // The optimistic in-memory edit was never persisted. Surface it inline and
    // revert the draft to the server's last-known list so the visible editor can't
    // silently drift from what is actually saved.
    const acct = state && (state.accounts || []).find((a) => a.id === id);
    if (acct && Array.isArray(acct.repos)) { draftReposByAcct[id] = acct.repos.slice(); renderRepoList(id); }
    repoErr(id, "Couldn't save repositories: " + String((e && e.message) || e));
  });
}

async function saveSettings() {
  const release = document.getElementById("release-input").value;
  const showDrafts = document.getElementById("s-drafts").checked;
  const notifications = {
    reviewRequested: document.getElementById("n-review").checked,
    readyToMerge: document.getElementById("n-ready").checked,
    changesRequested: document.getElementById("n-changes").checked,
    ciFailing: document.getElementById("n-ci").checked,
  };
  goView("queue", true);
  await withRefresh(() => postJSON("api/prefs", { release, showDrafts, notifications }));
}

async function rescanAccounts() {
  rescanning = true;
  const btn = document.getElementById("rescan-btn");
  if (btn) btn.classList.add("spin");
  try {
    const res = await fetch("api/accounts");
    const data = await readJson(res);
    state = data.dashboard; prefs = data.prefs; loadError = null;
  } catch (e) {
    loadError = String((e && e.message) || e);
  } finally { rescanning = false; render(); }
}

function dismissNotif(id, cardEl) {
  if (cardEl) cardEl.classList.add("removing");
  const go = () => withRefresh(() => postJSON("api/notifications/dismiss", { id }));
  setTimeout(go, 200);
}
const dismissAll = () => withRefresh(() => postJSON("api/notifications/dismiss-all"));
const restoreNotifs = () => withRefresh(() => postJSON("api/notifications/restore"));

/* ---- navigation ---- */

function goView(next, forward) {
  if (view === "accounts" && next !== "accounts") { for (const k in editingByAcct) editingByAcct[k] = -1; }
  prevRank = RANK[view] || 0;
  view = next;
  render(forward === undefined ? undefined : forward);
}

/* ---- cards ---- */

function pill(s) { return '<span class="pill ' + (s.tone || "muted") + '">' + esc(s.label) + "</span>"; }

function prCard(item) {
  const pr = item.pr;
  return '<a class="card" href="' + esc(pr.url) + '" target="_blank" rel="noreferrer">' +
    '<div class="card-top"><div class="card-title">' + esc(pr.title) + "</div></div>" +
    '<div class="card-sub">' +
      avatarTag(pr.authorAvatarUrl, pr.author, "avatar", 36) +
      '<span class="repo">' + esc(shortRepo(pr.repository)) + " #" + pr.number + "</span>" +
      "<span>by " + esc(pr.author) + "</span>" +
    "</div>" +
    (item.reason ? '<div class="reason">' + esc(item.reason) + "</div>" : "") +
    '<div class="pills">' + (item.signals || []).map(pill).join("") + "</div>" +
  "</a>";
}

function issueCard(item) {
  const is = item.issue;
  return '<a class="card" href="' + esc(is.url) + '" target="_blank" rel="noreferrer">' +
    '<div class="card-top"><div class="card-title">' + esc(is.title) + "</div></div>" +
    '<div class="card-sub">' +
      avatarTag(is.authorAvatarUrl, is.author, "avatar", 36) +
      '<span class="repo">' + esc(shortRepo(is.repository)) + " #" + is.number + "</span>" +
      "<span>by " + esc(is.author) + "</span>" +
    "</div>" +
    '<div class="pills">' + (item.signals || []).map(pill).join("") + "</div>" +
  "</a>";
}

function laneIcon(lane) {
  switch (lane.id) {
    case "review-queue": case "needs-review": case "assigned": return ICONS.eye;
    case "ready-to-merge": case "ready": return ICONS.merge;
    case "ci-failing": case "blocked": return ICONS.xcircle;
    case "unresolved": return ICONS.chat;
    case "your-prs": case "yours": case "in-progress": return ICONS.pr;
    case "triage": return ICONS.tag;
    case "active": return ICONS.clock;
    default: return ICONS.dot2;
  }
}

function laneHtml(lane) {
  const items = (lane.items || []).map((it) => (it.pr ? prCard(it) : issueCard(it))).join("");
  const tone = lane.tone || "muted";
  const repos = new Set((lane.items || []).map((it) => (it.pr || it.issue || {}).repository).filter(Boolean));
  const repoLabel = repos.size + (repos.size === 1 ? " repo" : " repos");
  const capped = typeof lane.cappedTotal === "number" && lane.cappedTotal > lane.items.length;
  const detail = capped
    ? "top " + lane.items.length + " of " + lane.cappedTotal
    : repoLabel;
  const collapsed = collapsedLanes.has(lane.id);
  return '<section class="lane' + (collapsed ? " collapsed" : "") + '" data-lane="' + esc(lane.id) + '">' +
    '<button class="lane-head" data-lane-toggle="' + esc(lane.id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      '<span class="lane-ico t-' + tone + '">' + laneIcon(lane) + "</span>" +
      '<span class="lane-title">' + esc(lane.label) + "</span>" +
      '<span class="lane-count">' + lane.items.length + "</span>" +
      '<span class="lane-detail' + (capped ? " capped" : "") + '">' + detail + "</span>" +
      '<span class="lane-caret">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="lane-body"><div class="inner"><div class="grid">' + items + "</div></div></div>" +
  "</section>";
}

/* ---- review attention board (full pr-dashboard parity) ---- */

function slugId(s) {
  return "b-" + String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
}

// Buckets and the long Community list start collapsed so the focused queue stays the
// headline. Seeded once; after that the user's own toggles win.
let reviewDefaultsSeeded = false;
function seedReviewCollapse(att) {
  if (reviewDefaultsSeeded) return;
  reviewDefaultsSeeded = true;
  for (const b of att.buckets || []) collapsedLanes.add(slugId(b.label));
  collapsedLanes.add("community");
  collapsedLanes.add("attention-breakdown");
}

function developerCountsHtml(list) {
  if (!list || !list.length) return "";
  const rows = list.map((d) =>
    '<div class="dev-row">' +
      avatarTag(d.avatarUrl || null, d.actor, "dev-av", 36) +
      '<div class="dev-main">' +
        '<span class="dev-name">' + esc(d.actor) + "</span>" +
        (d.latestUpdatedAt ? '<span class="dev-when">updated ' + timeAgo(d.latestUpdatedAt) + "</span>" : "") +
      "</div>" +
      '<span class="dev-count">' + d.openPullRequestCount + "</span>" +
    "</div>"
  ).join("");
  const activeCount = list.length;
  const totalPrs = list.reduce((n, d) => n + (d.openPullRequestCount || 0), 0);
  return collapsibleSect({
    id: "core-team",
    title: "Core team open PRs",
    icon: ICONS.usersSm,
    iconTone: "accent",
    count: totalPrs,
    note: activeCount + " active author" + (activeCount === 1 ? "" : "s"),
    subtitle: "Who is carrying open work across the loaded queue right now.",
    body: '<div class="dev-counts">' + rows + "</div>",
  });
}

// A collapsible secondary reference group (Community / Core team / Attention
// breakdown). Quieter header than the primary queues; collapse state persists.
function collapsibleSect(opts) {
  const id = opts.id;
  const collapsed = collapsedLanes.has(id);
  const count = (opts.count != null)
    ? '<span class="sect-count">' + esc(String(opts.count)) + "</span>" : "";
  return '<section class="sect-group collapsible' + (collapsed ? " collapsed" : "") + '" data-sect="' + esc(id) + '">' +
    '<button class="sect-head" data-collapse="' + esc(id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      (opts.icon ? '<span class="sect-icon t-' + (opts.iconTone || "muted") + '">' + opts.icon + "</span>" : "") +
      '<span class="sect-title">' + esc(opts.title) + "</span>" +
      (opts.note ? '<span class="sect-note">' + esc(opts.note) + "</span>" : "") +
      count +
      '<span class="sect-caret t-' + (opts.iconTone || "muted") + '">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="collapse-body"><div class="collapse-inner">' +
      (opts.subtitle ? '<p class="sect-sub">' + esc(opts.subtitle) + "</p>" : "") +
      (opts.body || "") +
    "</div></div>" +
  "</section>";
}

// A clean, always-expanded primary queue (For you / Needs attention / Your PRs
// outside). Masonry cards with a title, count metric, and descriptive subtitle.
function queuePanel(opts) {
  const items = opts.items || [];
  const n = items.length;
  const capped = typeof opts.cappedTotal === "number" && opts.cappedTotal > n;
  const metric = capped ? "top " + n + " of " + opts.cappedTotal : n + " shown";
  const body = n
    ? '<div class="grid">' + items.map((it) => (it.pr ? prCard(it) : issueCard(it))).join("") + "</div>"
    : '<div class="lane-empty">' + esc(opts.emptyText || "Nothing here right now.") + "</div>";
  const collapsed = collapsedLanes.has(opts.id);
  return '<section class="qpanel collapsible' + (collapsed ? " collapsed" : "") + '" data-q="' + esc(opts.id) + '">' +
    '<button class="qhead" data-collapse="' + esc(opts.id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      '<span class="qdot t-' + (opts.tone || "muted") + '">' + (opts.icon || ICONS.dot2) + "</span>" +
      '<span class="qtitle">' + esc(opts.title) + "</span>" +
      '<span class="qmetric' + (capped ? " capped" : "") + '">' + metric + "</span>" +
      '<span class="qcaret t-' + (opts.tone || "muted") + '">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="collapse-body"><div class="collapse-inner qbody">' +
      (opts.subtitle ? '<p class="qsub">' + esc(opts.subtitle) + "</p>" : "") +
      body +
    "</div></div>" +
  "</section>";
}

function reviewBoardHtml() {
  const att = state.attention;
  seedReviewCollapse(att);
  let html = "";

  // 1. For you — personalized headline of your highest-leverage actions.
  if (att.forMe && att.forMe.length) {
    html += queuePanel({
      id: "for-you", title: "For you", tone: "accent", icon: ICONS.sparkle,
      subtitle: "Your highest-leverage actions across every repo, pulled to the top of the queue.",
      items: att.forMe.slice(0, 6), cappedTotal: att.forMe.length,
    });
  }

  // 2. Needs attention — the focused, team-managed review queue (the headline).
  html += queuePanel({
    id: "needs-attention", title: "Needs attention", tone: "danger", icon: ICONS.alertSm,
    subtitle: "One actionable row per PR with fresh activity, waiting on a review or a merge.",
    items: att.focus || [], cappedTotal: att.focusTotal,
    emptyText: "Nothing is waiting on a reviewer right now \u00b7 anything blocked sits in the breakdown below.",
  });

  // 3. Your PRs outside Needs attention — the viewer's own out-of-queue work,
  //    sat directly under the focused queue so it's easy to see what got filtered.
  html += queuePanel({
    id: "outside-focus", title: "Your PRs outside Needs attention", tone: "info", icon: ICONS.pr,
    subtitle: "Open non-draft PRs you authored that do not currently qualify for the focused queue.",
    items: (att.focusExclusions || []).slice(0, 10), cappedTotal: (att.focusExclusions || []).length,
    emptyText: "None right now \u00b7 every open PR you authored is already in the queue or still in draft.",
  });

  // 4. Community — external-contributor PRs, collapsible reference group.
  if (att.community && att.community.length) {
    const repos = new Set(att.community.map((c) => (c.pr || {}).repository).filter(Boolean));
    html += collapsibleSect({
      id: "community",
      title: "Community",
      icon: ICONS.globe,
      iconTone: "success",
      count: att.community.length,
      note: "external contributors \u00b7 " + repos.size + (repos.size === 1 ? " repo" : " repos"),
      subtitle: "Recently active external-contributor PRs, tracked apart from the core-team queue.",
      body: '<div class="grid">' + att.community.map((it) => (it.pr ? prCard(it) : issueCard(it))).join("") + "</div>",
    });
  }

  // 5. Core team open PRs — who is carrying open work right now.
  html += developerCountsHtml(att.developerCounts);

  // 6. Attention breakdown — every signal lane, collapsed by default, at the bottom.
  const buckets = (att.buckets || []).filter((b) => b.items && b.items.length);
  if (buckets.length) {
    const total = buckets.reduce((n, b) => n + b.items.length, 0);
    html += collapsibleSect({
      id: "attention-breakdown",
      title: "Attention breakdown",
      icon: ICONS.layers,
      iconTone: "warning",
      count: total,
      note: buckets.length + " lane" + (buckets.length === 1 ? "" : "s"),
      subtitle: "Every signal lane behind the queue. Expand one to see the PRs it groups.",
      body: buckets.map((b) => laneHtml({ id: slugId(b.label), label: b.label, tone: b.tone, items: b.items })).join(""),
    });
  }

  return html;
}

/* ---- topbar ---- */

function tabs() {
  const mode = state ? state.mode : "review";
  const defs = [["review", "Review"], ["issues", "Issues"], ["ship", "Ship"]];
  return '<div class="tabs">' + defs.map(([id, label]) =>
    '<button class="tab ' + (mode === id ? "active" : "") + '" data-mode="' + id + '">' + label + "</button>"
  ).join("") + "</div>";
}

function avatarStack(accts, size) {
  if (!accts || !accts.length) return "";
  const shown = accts.slice(0, 3);
  return '<span class="stack">' + shown.map((a) =>
    '<span class="stk-av' + (a.enterprise ? " ent" : "") + '" title="' +
      esc(a.login + (a.enterprise ? " \u00b7 " + (a.host || "Enterprise") : "")) + '">' +
      acctAvatar(a, size) +
      (a.enterprise ? '<span class="ent-dot" title="Enterprise">' + ICONS.building + "</span>" : "") +
    "</span>"
  ).join("") + (accts.length > shown.length ? '<span class="stk-more">+' + (accts.length - shown.length) + "</span>" : "") + "</span>";
}

function accountChip() {
  const active = (state && state.activeAccounts) || [];
  const anyEnt = active.some((a) => a.enterprise);
  let inner;
  if (!active.length) {
    inner = '<span class="sdot bg-muted"></span><span class="name">Accounts</span>';
  } else if (active.length === 1) {
    inner = avatarStack(active, 40) + '<span class="name">' + esc(active[0].login) + "</span>" +
      (anyEnt ? '<span class="ent-badge sm" title="GitHub Enterprise">' + ICONS.building + "Enterprise</span>" : "");
  } else {
    inner = avatarStack(active, 40) + '<span class="name">' + active.length + " accounts</span>" +
      (anyEnt ? '<span class="ent-badge sm" title="Includes a GitHub Enterprise account">' + ICONS.building + "</span>" : "");
  }
  return '<button class="acct-chip ' + (view === "accounts" ? "active" : "") + '" id="acct-btn" title="GitHub accounts">' +
    inner + ICONS.chev + "</button>";
}

function topbarHtml() {
  const notifCount = (state && state.notifications || []).length;
  const left = view === "queue"
    ? tabs()
    : '<button class="backbtn" id="back-btn">' + ICONS.back + "Back</button>";
  const right =
    (state ? accountChip() : "") +
    '<div class="tb-actions">' +
    '<button class="iconbtn ' + (refreshing ? "spin" : "") + '" id="refresh-btn" title="Refresh">' + ICONS.refresh + "</button>" +
    '<button class="iconbtn ' + (view === "notifications" ? "active" : "") + '" id="bell-btn" title="Notifications">' + ICONS.bell +
      (notifCount ? '<span class="badge">' + notifCount + "</span>" : "") + "</button>" +
    '<button class="iconbtn ' + (view === "settings" ? "active" : "") + '" id="gear-btn" title="Settings">' + ICONS.gear + "</button>" +
    "</div>";
  return '<div class="topbar" id="topbar">' +
    '<button class="brand" id="brand-home" type="button" title="Back to review queue"><span class="mark">' + LOGO + '</span><span class="brand-text">Aspire Team App</span></button>' +
    left + '<span class="spacer"></span>' + right + "</div>";
}

/* ---- views ---- */

function queueView() {
  const active = state.activeAccounts || [];
  const anyEnt = active.some((a) => a.enterprise);
  const whoLabel = active.length > 1
    ? esc(state.viewer) + ' <span class="who-more">+' + (active.length - 1) + " more</span>"
    : esc(state.viewer);
  const who = active.length
    ? avatarStack(active, 44) + '<span class="who-name">' + whoLabel + "</span>" +
        (anyEnt ? '<span class="ent-badge" title="GitHub Enterprise account active">' + ICONS.building + "Enterprise</span>" : "")
    : '<span class="who-name">' + esc(state.viewer) + "</span>";
  const isReviewBoard = state.mode === "review" && state.attention;
  const lanesHtml = isReviewBoard
    ? reviewBoardHtml()
    : (state.lanes.length
        ? state.lanes.map(laneHtml).join("")
        : '<div class="state"><div class="ico">' + ICONS.check + "</div><h2>All clear</h2><p>No items in " + esc(state.mode) + " mode for your watched repositories.</p></div>");
  const draftsHidden = !state.showDrafts && state.counts && state.counts.drafts
    ? ' <span class="meta-draft">\u00b7 ' + state.counts.drafts + " draft" + (state.counts.drafts === 1 ? "" : "s") + " hidden</span>"
    : "";
  return '<div class="subbar">' +
      '<span class="who">' + who + "</span>" +
      '<span class="meta">' + state.counts.prs + " open PRs across " + state.repos.length + " repos, updated " + timeAgo(state.fetchedAt) + draftsHidden + "</span>" +
      statsHtml() +
    "</div>" +
    (state.errors && state.errors.length ? '<div class="errbar">' + esc(state.errors.join(" \u00b7 ")) + "</div>" : "") +
    '<div class="lanes">' + lanesHtml + "</div>";
}

function statsHtml() {
  const c = state.counts;
  // In review mode the header mirrors the attention board so the numbers agree.
  if (state.mode === "review" && state.attention) {
    const att = state.attention;
    const bucket = (label) => {
      const b = (att.buckets || []).find((x) => x.label === label);
      return b ? b.items.length : 0;
    };
    return '<div class="stats">' +
      '<span class="stat"><span class="dot bg-danger"></span><b>' + att.focusTotal + "</b> needs attention</span>" +
      '<span class="stat"><span class="dot bg-success"></span><b>' + bucket("Ready to merge") + "</b> ready</span>" +
      '<span class="stat"><span class="dot bg-warning"></span><b>' + bucket("CI failing") + "</b> CI failing</span>" +
      '<span class="stat"><span class="dot bg-accent"></span><b>' + (att.community ? att.community.length : 0) + "</b> community</span>" +
    "</div>";
  }
  return '<div class="stats">' +
    '<span class="stat"><span class="dot bg-danger"></span><b>' + c.needsReview + "</b> needs review</span>" +
    '<span class="stat"><span class="dot bg-success"></span><b>' + c.readyToMerge + "</b> ready</span>" +
    '<span class="stat"><span class="dot bg-warning"></span><b>' + c.ciFailing + "</b> CI failing</span>" +
  "</div>";
}

function toggle(id, title, desc, checked) {
  return '<div class="toggle-row"><span><span class="tl">' + esc(title) + "</span>" +
    (desc ? '<span class="td">' + esc(desc) + "</span>" : "") + "</span>" +
    '<label class="switch"><input type="checkbox" id="' + id + '" ' + (checked ? "checked" : "") + ' /><span class="slider"></span></label></div>';
}

function settingsView() {
  const n = prefs.notifications;
  const limit = (state.reviewLimit || 10);
  return '<div class="page">' +
    '<div class="page-head"><h2>Settings</h2><p>Tune the shared review queue, the ship milestone, and when the canvas speaks up. Watched repositories are configured per account in the Accounts tab.</p></div>' +
    '<div class="section"><h3>Review queue</h3>' +
      '<p class="hint">The shared queue is team-managed, not individually sorted. It shows at most <b>' + limit + '</b> PRs, ranked so the oldest waits surface first.</p>' +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.eye + '<span>A PR only enters the shared queue once <b>checks are green</b> and <b>all review feedback is resolved</b>. Unfinished work stays in the author\u2019s <b>Your PRs</b> lane.</span></div>' +
        '<div class="policy-row">' + ICONS.pr + '<span>Draft PRs are hidden by default. They are prototypes and experiments, not review work.</span></div>' +
      "</div>" +
      toggle("s-drafts", "Show draft PRs", "Include drafts in lanes and counts", !!state.showDrafts) +
    "</div>" +
    '<div class="section"><h3>Ship milestone</h3>' +
      '<p class="hint">Used by Ship mode to group work for the active release.</p>' +
      '<div class="field"><input type="text" id="release-input" value="' + esc(prefs.release || "") + '" placeholder="13.4" /></div></div>' +
    '<div class="section" id="notif-settings"><h3>Notifications</h3>' +
      '<p class="hint">Live in-session alerts surface in the bell. Choose what counts.</p>' +
      toggle("n-review", "Review requested", "Someone asked you to review a PR", n.reviewRequested) +
      toggle("n-ready", "Your PR is ready to merge", "Approved with passing checks", n.readyToMerge) +
      toggle("n-changes", "Changes requested on your PR", "A reviewer wants edits", n.changesRequested) +
      toggle("n-ci", "CI failing on your PR", "A required check is red", n.ciFailing) +
    "</div>" +
    '<div class="row-actions">' +
      '<button class="btn ghost" id="cancel-settings">Cancel <kbd>Esc</kbd></button>' +
      '<button class="btn" id="save-settings">Save changes <kbd>\u21B5</kbd></button></div>' +
  "</div>";
}

function srcRow(s) {
  const t = (ACCT_STATUS[s.status] || ACCT_STATUS.failed);
  const meta = [];
  if (s.status !== "failed") meta.push("<span>" + s.accessible + "/" + s.total + " repos</span>");
  if (s.scopes && s.scopes.includes("read:org")) meta.push('<span class="scopes">read:org</span>');
  if (s.reason) meta.push("<span>" + esc(s.reason) + "</span>");
  return '<div class="src-row">' +
    '<span class="dot bg-' + t.tone + '"></span>' +
    '<span class="sname">' + esc(srcLabel(s.source)) +
      (s.enterprise ? '<span class="schip ent">' + esc(s.host || "Enterprise") + "</span>" : "") +
      (s.chosen ? '<span class="schip">IN USE</span>' : "") + "</span>" +
    '<span class="smeta"><span class="t-' + t.tone + '">' + esc(t.label) + "</span>" + meta.join("") + "</span>" +
  "</div>";
}

function repoEditorHtml(a) {
  const id = a.id;
  const count = (draftReposByAcct[id] || a.repos || []).length;
  return '<div class="acct-repos">' +
    '<div class="acct-repos-head"><span>Watched repositories</span><span class="rcount" data-rcount="' + esc(id) + '">' + count + "</span></div>" +
    '<p class="acct-repos-hint">Add a repo as <code>owner/repo</code>, then press Enter or the plus. Changes save to this account immediately.</p>' +
    '<div class="repo-add">' +
      '<input class="repo-add-input" data-addinput="' + esc(id) + '" type="text" spellcheck="false" autocomplete="off" autocapitalize="off" placeholder="owner/repo" />' +
      '<button class="repo-add-btn" data-add="' + esc(id) + '" title="Add repository" aria-label="Add repository">' + ICONS.plus + "</button>" +
    "</div>" +
    '<div class="repo-err" data-err="' + esc(id) + '"></div>' +
    '<ul class="repo-list" data-list="' + esc(id) + '">' + repoRowsHtml(id) + "</ul>" +
  "</div>";
}

function accountCard(a, asPicker) {
  const tone = acctTone(a);
  const st = (ACCT_STATUS[a.status] || ACCT_STATUS.failed);
  const usable = a.status !== "failed";
  // Seed this account's repo draft from the server copy unless mid-edit.
  if (editingByAcct[a.id] == null || editingByAcct[a.id] < 0) draftReposByAcct[a.id] = (a.repos || []).slice();
  if (editingByAcct[a.id] == null) editingByAcct[a.id] = -1;
  const open = expanded.has(a.id) || asPicker;
  const kinds = a.sourceKinds || [...new Set((a.sources || []).map((s) => s.source))];
  const badgeHtml = kinds.map((k) => '<span class="src-badge">' + esc(srcLabel(k)) + "</span>").join("");
  const multi = (a.sources || []).length > 1;
  const entBadge = a.enterprise
    ? '<span class="ent-badge" title="' + esc(a.host || "GitHub Enterprise") + '">' + ICONS.building + "Enterprise</span>"
    : "";
  const meta = [];
  if (usable) meta.push("<span>" + a.accessible + "/" + a.total + " repos</span>");
  if (a.hasReadOrg) meta.push('<span class="scopes">read:org</span>');
  if (a.reason) meta.push("<span>" + esc(a.reason) + "</span>");
  const detail = (a.sources || []).map(srcRow).join("");
  const sw = '<label class="switch" title="' + (a.active ? "Active \u00b7 click to disable" : "Enable this account") + '">' +
    '<input type="checkbox" data-active="' + esc(a.id) + '"' + (a.active ? " checked" : "") + (usable ? "" : " disabled") +
    ' aria-label="Toggle ' + esc(a.login) + '" /><span class="slider"></span></label>';
  return '<div class="acct-card ' + (a.active ? "active " : "") + (open ? "open" : "") + '" data-card="' + esc(a.id) + '">' +
    '<div class="acct-head">' +
      '<button class="acct-main" data-expand="' + esc(a.id) + '">' +
        acctAvatar(a, 72) +
        '<span class="acct-id">' +
          '<span class="acct-name">' + esc(a.login) + entBadge + badgeHtml +
            (multi ? '<span class="count">found in ' + (a.sources || []).length + " places</span>" : "") + "</span>" +
          '<span class="acct-meta"><span class="acct-status"><span class="dot bg-' + tone + '"></span><span class="t-' + tone + '">' + esc(st.label) + "</span></span>" + meta.join("") + "</span>" +
        "</span>" +
      "</button>" +
      '<div class="acct-right">' + sw +
        '<button class="caret" data-expand="' + esc(a.id) + '" title="Configure" aria-label="Configure account">' + ICONS.chev + "</button>" +
      "</div>" +
    "</div>" +
    '<div class="acct-detail"><div class="inner">' +
      repoEditorHtml(a) +
      (detail ? '<div class="src-list">' + detail + "</div>" : "") +
    "</div></div>" +
  "</div>";
}

function accountsView() {
  const accts = (state && state.accounts) || [];
  const activeCount = accts.filter((a) => a.active).length;
  const rows = accts.length
    ? '<div class="acct-list">' + accts.map((a) => accountCard(a, false)).join("") + "</div>"
    : '<p class="acct-intro">No GitHub credentials detected. Run <code>gh auth login</code> and rescan.</p>';
  return '<div class="page">' +
    '<div class="page-head" style="display:flex;align-items:flex-end;gap:10px">' +
      '<div><h2>GitHub accounts</h2><p>Enable any number of accounts. Their results interleave across all tabs, and each account watches its own repositories.</p></div>' +
      '<div class="page-actions"><button class="rescan-btn ' + (rescanning ? "spin" : "") + '" id="rescan-btn">' + ICONS.refresh + "Rescan</button></div>" +
    "</div>" +
    (accts.length ? '<p class="acct-intro">' + activeCount + " of " + accts.length + " account" + (accts.length === 1 ? "" : "s") + " active.</p>" : "") +
    rows +
  "</div>";
}

function notificationsView() {
  const items = state.notifications || [];
  const dismissed = state.dismissedCount || 0;
  let body;
  if (!items.length) {
    body = '<div class="state"><div class="ico">' + ICONS.bellBig + "</div><h2>You're all caught up</h2>" +
      "<p>Nothing needs your attention right now. Adjust what counts as a notification in Settings.</p>" +
      '<div class="state-cta"><button class="btn ghost" id="to-settings">Open settings</button>' +
      (dismissed ? '<button class="btn ghost" id="restore-notifs">Restore ' + dismissed + " dismissed</button>" : "") + "</div></div>";
  } else {
    body = '<div class="notif-list">' + items.map((n) =>
      '<div class="notif-card" data-id="' + esc(n.id) + '">' +
        '<span class="ndot bg-' + (n.tone || "muted") + '"></span>' +
        '<a class="nbody" href="' + esc(n.url) + '" target="_blank" rel="noreferrer">' +
          '<span class="ntitle">' + esc(n.title) + "</span>" +
          '<span class="ndetail">' + esc(n.detail) + ' \u00b7 <span class="repo">' + esc(shortRepo(n.repository)) + " #" + n.number + "</span></span>" +
        "</a>" +
        '<button class="dismiss" data-dismiss="' + esc(n.id) + '" title="Dismiss" aria-label="Dismiss">' + ICONS.x + "</button>" +
      "</div>"
    ).join("") + "</div>";
  }
  return '<div class="page">' +
    '<div class="page-head" style="display:flex;align-items:flex-end;gap:10px">' +
      '<div><h2>Notifications</h2><p>' + (items.length ? items.length + " active" : "Up to date") +
        (dismissed ? ", " + dismissed + " dismissed" : "") + ".</p></div>" +
      '<div class="page-actions">' +
        (items.length ? '<button class="rescan-btn" id="dismiss-all">Clear all</button>' : "") +
        (dismissed && items.length ? '<button class="rescan-btn" id="restore-notifs2">Restore</button>' : "") +
      "</div>" +
    "</div>" + body +
    '<div class="notif-foot"><button class="linklike" id="notif-config" type="button">' + ICONS.gear + ' Configure which notifications count</button></div>' +
  "</div>";
}

function authPicker() {
  const accts = state.accounts || [];
  const picker = accts.length
    ? '<div class="acct-list" style="text-align:left;margin-top:18px">' + accts.map((a) => accountCard(a, true)).join("") + "</div>"
    : '<span class="cmd">gh auth login</span>';
  return '<div class="page" style="max-width:560px">' +
    '<div class="state" style="padding-top:32px"><div class="ico">' + ICONS.users + "</div>" +
    "<h2>Enable a GitHub account</h2><p>" + esc(state.message) + "</p></div>" +
    picker +
  "</div>";
}

/* ---- render ---- */

function render(forward) {
  app.removeAttribute("aria-busy");

  if (loadError && !state) {
    app.innerHTML = topbarShell() +
      '<div class="state"><div class="ico">' + ICONS.alert + '</div><h2>Could not load</h2><p>' + esc(loadError) +
      '</p><div class="state-cta"><button class="btn" id="retry-btn">Try again</button></div></div>';
    const rt = document.getElementById("retry-btn"); if (rt) rt.addEventListener("click", load);
    return;
  }
  if (!state) return; // skeleton (initial HTML) stays until first load resolves

  let inner;
  if (!state.authenticated) { view = "queue"; inner = authPicker(); }
  else if (view === "settings") inner = settingsView();
  else if (view === "accounts") inner = accountsView();
  else if (view === "notifications") inner = notificationsView();
  else inner = queueView();

  const dir = forward === false || (forward === undefined && (RANK[view] || 0) < prevRank) ? "back" : "";
  // A refresh/rescan that fails after the dashboard already loaded sets loadError
  // but keeps the last-good state. Surface it as a dismissible banner instead of
  // discarding the loaded UI (the full-screen "Could not load" state above only
  // applies to the very first load, when there is no state to preserve).
  const banner = loadError
    ? '<div class="errbar loaderr" role="alert">' + esc(loadError) +
      '<button class="errbar-x" id="load-errbar-dismiss" type="button" title="Dismiss" aria-label="Dismiss">' + ICONS.x + "</button></div>"
    : "";
  app.innerHTML = topbarHtml() + banner + '<div class="viewport"><div class="view ' + dir + '">' + inner + "</div></div>";
  app.classList.toggle("loading", refreshing);
  if (banner) {
    const bx = document.getElementById("load-errbar-dismiss");
    if (bx) bx.addEventListener("click", function () { loadError = null; render(); });
  }
  wire();
  layoutGrids();
}

/* ---- masonry: fill the left column first, top-to-bottom ----
   CSS multi-column balances column heights, which puts a single tall card on
   the left and the rest on the right. For a prioritized queue we want the
   opposite: read straight down the first column, then the next. We rebuild each
   .grid into real flex columns and distribute cards in queue order, only
   reflowing when the responsive column count actually changes. */
var __gridRO = null;

function ensureGridObserver() {
  if (__gridRO || typeof ResizeObserver === "undefined") return;
  __gridRO = new ResizeObserver(function (entries) {
    for (var i = 0; i < entries.length; i++) layoutGrid(entries[i].target);
  });
}

function layoutGrids() {
  ensureGridObserver();
  if (__gridRO) __gridRO.disconnect();
  var grids = document.querySelectorAll(".grid");
  for (var i = 0; i < grids.length; i++) {
    layoutGrid(grids[i]);
    if (__gridRO) __gridRO.observe(grids[i]);
  }
}

function layoutGrid(grid) {
  var cards = grid.__cards;
  if (!cards) {
    cards = [];
    var kids = grid.children;
    for (var i = 0; i < kids.length; i++) {
      if (kids[i].classList && kids[i].classList.contains("card")) cards.push(kids[i]);
    }
    if (!cards.length) return; // skeletons or non-card grids: leave alone
    grid.__cards = cards;
  }
  var w = grid.clientWidth;
  if (!w) return; // not visible yet; the observer fires again when it is
  var COLW = 280, GAP = 12;
  var cols = Math.max(1, Math.floor((w + GAP) / (COLW + GAP)));
  if (cols > cards.length) cols = cards.length || 1;
  if (grid.__cols === cols) return; // responsive column count unchanged
  grid.__cols = cols;

  while (grid.firstChild) grid.removeChild(grid.firstChild);
  if (cols <= 1) {
    grid.classList.remove("mcol");
    for (var j = 0; j < cards.length; j++) grid.appendChild(cards[j]);
    return;
  }
  grid.classList.add("mcol");
  var n = cards.length, base = Math.floor(n / cols), extra = n % cols, idx = 0;
  for (var c = 0; c < cols; c++) {
    var col = document.createElement("div");
    col.className = "mcol-col";
    var cnt = base + (c < extra ? 1 : 0);
    for (var k = 0; k < cnt; k++) col.appendChild(cards[idx++]);
    grid.appendChild(col);
  }
}

function topbarShell() {
  return '<div class="topbar"><span class="brand"><span class="mark">' + LOGO + '</span><span class="brand-text">Aspire Team App</span></span><span class="spacer"></span></div>';
}

/* ---- watched-repository editor ---- */

var REPO_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*\/[A-Za-z0-9][A-Za-z0-9_.-]*$/;

function normRepo(v) {
  return String(v || "").trim()
    .replace(/^https?:\/\/github\.com\//i, "")
    .replace(/\.git$/i, "")
    .replace(/\/+$/, "");
}

function repoErr(id, msg) {
  var el = document.querySelector('.repo-err[data-err="' + cssEsc(id) + '"]');
  if (!el) return;
  if (!msg) { el.textContent = ""; el.classList.remove("show"); return; }
  el.textContent = msg; el.classList.add("show");
}

function shake(el) {
  if (!el) return;
  el.classList.remove("shake");
  void el.offsetWidth;
  el.classList.add("shake");
}

function repoRowsHtml(id) {
  var repos = draftReposByAcct[id] || [];
  var editing = editingByAcct[id];
  if (!repos.length) {
    return '<li class="repo-empty">No repositories yet. Add one above to start watching it.</li>';
  }
  return repos.map(function (r, i) {
    if (i === editing) {
      return '<li class="repo-row editing" data-acct="' + esc(id) + '" data-i="' + i + '">' +
        '<input class="repo-edit-input" data-editinput="' + esc(id) + '" type="text" spellcheck="false" autocomplete="off" value="' + esc(r) + '" />' +
        '<span class="repo-acts">' +
          '<button class="repo-ico ok" data-save-edit="' + i + '" data-acct="' + esc(id) + '" title="Save" aria-label="Save">' + ICONS.check + "</button>" +
          '<button class="repo-ico" data-cancel-edit="' + i + '" data-acct="' + esc(id) + '" title="Cancel" aria-label="Cancel">' + ICONS.x + "</button>" +
        "</span></li>";
    }
    return '<li class="repo-row" data-acct="' + esc(id) + '" data-i="' + i + '">' +
      '<span class="repo-name">' + esc(r) + "</span>" +
      '<span class="repo-acts">' +
        '<button class="repo-ico" data-edit="' + i + '" data-acct="' + esc(id) + '" title="Edit" aria-label="Edit">' + ICONS.pencil + "</button>" +
        '<button class="repo-ico danger" data-del="' + i + '" data-acct="' + esc(id) + '" title="Remove" aria-label="Remove">' + ICONS.trash + "</button>" +
      "</span></li>";
  }).join("");
}

function updateRepoCount(id) {
  var c = document.querySelector('.rcount[data-rcount="' + cssEsc(id) + '"]');
  if (c) c.textContent = (draftReposByAcct[id] || []).length;
}

function renderRepoList(id, flagLast) {
  var ul = document.querySelector('.repo-list[data-list="' + cssEsc(id) + '"]');
  if (!ul) return;
  ul.innerHTML = repoRowsHtml(id);
  updateRepoCount(id);
  if (flagLast) {
    var rows = ul.querySelectorAll(".repo-row");
    var last = rows[rows.length - 1];
    if (last) { last.classList.add("added"); last.addEventListener("animationend", function () { last.classList.remove("added"); }, { once: true }); }
  }
  wireRepoRows(id);
}

function addRepoFromInput(id) {
  var inp = document.querySelector('.repo-add-input[data-addinput="' + cssEsc(id) + '"]');
  if (!inp) return;
  var v = normRepo(inp.value);
  if (!v) { return; }
  if (!REPO_RE.test(v)) { repoErr(id, "Use the owner/repo format, like microsoft/aspire."); shake(inp); return; }
  var list = draftReposByAcct[id] || (draftReposByAcct[id] = []);
  if (list.some(function (r) { return r.toLowerCase() === v.toLowerCase(); })) {
    repoErr(id, v + " is already in the list."); shake(inp); return;
  }
  list.push(v);
  inp.value = "";
  repoErr(id, "");
  renderRepoList(id, true);
  persistAccountRepos(id);
  inp.focus();
}

function commitEdit(id, i) {
  var inp = document.querySelector('.repo-edit-input[data-editinput="' + cssEsc(id) + '"]');
  if (!inp) return;
  var v = normRepo(inp.value);
  var list = draftReposByAcct[id] || [];
  if (!REPO_RE.test(v)) { repoErr(id, "Use the owner/repo format, like microsoft/aspire."); shake(inp); return; }
  if (list.some(function (r, j) { return j !== i && r.toLowerCase() === v.toLowerCase(); })) {
    repoErr(id, v + " is already in the list."); shake(inp); return;
  }
  list[i] = v; editingByAcct[id] = -1; repoErr(id, "");
  renderRepoList(id);
  persistAccountRepos(id);
}

function deleteRepo(id, i, row) {
  // The row-removal animation is our cue to actually splice, but a missed
  // animationend (reduced motion, a backgrounded tab, an interrupted animation)
  // would strand the row. We keep a fallback timer as a backstop, so both the
  // animationend handler and the timer can fire. A once-only guard makes the splice
  // run exactly once: without it the second call would splice a now-shifted index
  // and silently drop the wrong repository.
  var ran = false;
  var fallback = null;
  var done = function () {
    if (ran) return;
    ran = true;
    if (fallback) clearTimeout(fallback);
    (draftReposByAcct[id] || []).splice(i, 1);
    if (editingByAcct[id] === i) editingByAcct[id] = -1;
    renderRepoList(id);
    persistAccountRepos(id);
  };
  if (row) { row.classList.add("removing"); row.addEventListener("animationend", done, { once: true }); fallback = setTimeout(done, 240); }
  else done();
}

function wireRepoRows(id) {
  var ul = document.querySelector('.repo-list[data-list="' + cssEsc(id) + '"]');
  if (!ul) return;
  ul.querySelectorAll("[data-edit]").forEach(function (b) {
    b.addEventListener("click", function () {
      editingByAcct[id] = parseInt(b.dataset.edit, 10); repoErr(id, ""); renderRepoList(id);
      var ei = document.querySelector('.repo-edit-input[data-editinput="' + cssEsc(id) + '"]'); if (ei) { ei.focus(); ei.select(); }
    });
  });
  ul.querySelectorAll("[data-del]").forEach(function (b) {
    var fired = false;
    b.addEventListener("click", function () { if (fired) return; fired = true; deleteRepo(id, parseInt(b.dataset.del, 10), b.closest(".repo-row")); });
  });
  ul.querySelectorAll("[data-save-edit]").forEach(function (b) {
    b.addEventListener("click", function () { commitEdit(id, parseInt(b.dataset.saveEdit, 10)); });
  });
  ul.querySelectorAll("[data-cancel-edit]").forEach(function (b) {
    b.addEventListener("click", function () { editingByAcct[id] = -1; repoErr(id, ""); renderRepoList(id); });
  });
  var ei = ul.querySelector(".repo-edit-input");
  if (ei) ei.addEventListener("keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); commitEdit(id, editingByAcct[id]); }
    else if (e.key === "Escape") { e.preventDefault(); editingByAcct[id] = -1; repoErr(id, ""); renderRepoList(id); }
  });
}

function wireRepoEditor(id) {
  var addBtn = document.querySelector('.repo-add-btn[data-add="' + cssEsc(id) + '"]');
  if (addBtn) addBtn.addEventListener("click", function () { addRepoFromInput(id); });
  var inp = document.querySelector('.repo-add-input[data-addinput="' + cssEsc(id) + '"]');
  if (inp) inp.addEventListener("keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); addRepoFromInput(id); }
    else { repoErr(id, ""); }
  });
  wireRepoRows(id);
}

function wire() {
  document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => setMode(b.dataset.mode)));
  const rb = document.getElementById("refresh-btn"); if (rb) rb.addEventListener("click", refresh);
  const back = document.getElementById("back-btn"); if (back) back.addEventListener("click", () => goView("queue", false));
  const bell = document.getElementById("bell-btn"); if (bell) bell.addEventListener("click", () => goView(view === "notifications" ? "queue" : "notifications"));
  const gear = document.getElementById("gear-btn"); if (gear) gear.addEventListener("click", () => goView(view === "settings" ? "queue" : "settings"));
  const acct = document.getElementById("acct-btn"); if (acct) acct.addEventListener("click", () => goView(view === "accounts" ? "queue" : "accounts"));

  const save = document.getElementById("save-settings"); if (save) save.addEventListener("click", saveSettings);
  const cancel = document.getElementById("cancel-settings"); if (cancel) cancel.addEventListener("click", () => goView("queue", false));

  // The Esc/Enter hints on the settings buttons are real shortcuts, like the app's
  // Cancel/Continue. Bound once so re-renders don't stack handlers.
  if (!keysBound) {
    keysBound = true;
    document.addEventListener("keydown", function (e) {
      if (view !== "settings") return;
      if (e.key === "Escape") { e.preventDefault(); goView("queue", false); }
      else if (e.key === "Enter" && !e.isComposing && (e.target.tagName || "") !== "TEXTAREA") {
        e.preventDefault(); saveSettings();
      }
    });
  }

  const rescan = document.getElementById("rescan-btn"); if (rescan) rescan.addEventListener("click", rescanAccounts);

  wireAccounts();

  document.querySelectorAll(".lane-head").forEach((b) =>
    b.addEventListener("click", () => {
      const id = b.dataset.laneToggle;
      const sec = b.closest(".lane");
      if (!sec) return;
      const nowCollapsed = !sec.classList.contains("collapsed");
      sec.classList.toggle("collapsed", nowCollapsed);
      b.setAttribute("aria-expanded", nowCollapsed ? "false" : "true");
      if (nowCollapsed) collapsedLanes.add(id); else collapsedLanes.delete(id);
    }));

  // Generic collapsible sections (primary queues + secondary reference groups).
  document.querySelectorAll("[data-collapse]").forEach((b) =>
    b.addEventListener("click", () => {
      const id = b.dataset.collapse;
      const sec = b.closest(".collapsible");
      if (!sec) return;
      const nowCollapsed = !sec.classList.contains("collapsed");
      sec.classList.toggle("collapsed", nowCollapsed);
      b.setAttribute("aria-expanded", nowCollapsed ? "false" : "true");
      if (nowCollapsed) collapsedLanes.add(id); else collapsedLanes.delete(id);
    }));

  document.querySelectorAll(".dismiss").forEach((b) =>
    b.addEventListener("click", (e) => { e.preventDefault(); e.stopPropagation(); dismissNotif(b.dataset.dismiss, b.closest(".notif-card")); }));
  const da = document.getElementById("dismiss-all"); if (da) da.addEventListener("click", dismissAll);
  const r1 = document.getElementById("restore-notifs"); if (r1) r1.addEventListener("click", restoreNotifs);
  const r2 = document.getElementById("restore-notifs2"); if (r2) r2.addEventListener("click", restoreNotifs);
  const ts = document.getElementById("to-settings"); if (ts) ts.addEventListener("click", () => goView("settings"));
  const brandHome = document.getElementById("brand-home"); if (brandHome) brandHome.addEventListener("click", () => goView("queue", false));
  const notifCfg = document.getElementById("notif-config");
  if (notifCfg) notifCfg.addEventListener("click", () => {
    goView("settings");
    requestAnimationFrame(() => {
      const sec = document.getElementById("notif-settings");
      if (sec) sec.scrollIntoView({ behavior: "smooth", block: "start" });
    });
  });
}

function wireAccounts() {
  // Active toggles interleave/withdraw an account's results across every tab.
  document.querySelectorAll("input[data-active]").forEach((inp) =>
    inp.addEventListener("change", () => { if (!inp.disabled) toggleAccountActive(inp.dataset.active, inp.checked); }));

  // Expand/collapse the account detail (repo editor + credential sources).
  document.querySelectorAll("[data-expand]").forEach((b) =>
    b.addEventListener("click", (e) => {
      e.stopPropagation();
      const id = b.dataset.expand;
      const card = document.querySelector('.acct-card[data-card="' + cssEsc(id) + '"]');
      if (!card) return;
      if (expanded.has(id)) { expanded.delete(id); card.classList.remove("open"); }
      else { expanded.add(id); card.classList.add("open"); }
    }));

  // Per-account watched-repository editors.
  document.querySelectorAll(".acct-card").forEach((card) => {
    const id = card.dataset.card;
    if (id) wireRepoEditor(id);
  });
}

try {
  const es = new EventSource("events");
  es.addEventListener("refresh", () => load());
} catch {}

load();
`;

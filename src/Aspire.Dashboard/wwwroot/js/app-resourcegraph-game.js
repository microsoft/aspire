// Aspire Outage: an arcade game played on the resource graph.
//
// The Play button of the graph, or the O key while the graph has focus, morphs the graph's resources into a formation of
// enemies. The player's ship is the Aspire logo. Escape or the Exit button morphs everything back into the graph.
//
// The game is self-contained plain JavaScript drawn on a canvas overlay. It never changes the graph's data or d3 state:
// while it runs it only sets inline opacity/visibility on the graph SVG and its controls, and restores the previous
// style attributes on exit. Until the game is launched there is no animation frame, no keyboard listener and no audio
// context, only the listeners that launch it.
//
// Hard core is a mode picked on the start screen in which the game has real consequences: destroying an enemy stops its
// resource, and clearing the wave starts the stopped resources again. It is off every time the game opens.
//
// Pure logic (formation, scoring, collisions, firewalls, best score storage, resource commands) is exported so it can be
// tested without a browser. See tests/Aspire.Dashboard.Components.Tests/JavaScript/ResourceGraphGame.test.mjs.

const TAU = Math.PI * 2;

const BEST_SCORE_KEY = 'aspire-resource-graph-game-best-score';
const MUTED_KEY = 'aspire-resource-graph-game-muted';

export const MAX_ENEMIES = 66;
const MAX_COLUMNS = 11;
const EXTRA_LIFE_SCORE = 2500;
const MAX_LIVES = 6;

// World units are "big pixels". The canvas is rendered at this low resolution and scaled up by CSS with
// image-rendering: pixelated, which keeps every sprite, line and letter crisp.
const SPRITE = 12;
const SPACING_X = 18;
const SPACING_Y = 18;
const HUD_HEIGHT = 14;
// The largest formation (11 columns, 6 rows) needs 192x108 units. These leave it room to march sideways and several rows
// to descend before reaching the ship, whatever the shape of the graph.
const MIN_WORLD_WIDTH = 240;
const MIN_WORLD_HEIGHT = 250;
const BACKGROUND_ALPHA = 0.94;

const COMMAND_ATTEMPTS = 10;
const COMMAND_RETRY_MS = 1000;

// Morph timings in milliseconds. Reduced motion replaces the flight with a short cross-fade.
const MORPH_PIXELATE = 350;
const MORPH_FLIGHT = 800;
const MORPH_STAGGER = 15;
const MORPH_OUT = 900;
const REDUCED_MORPH = 220;

const DESTROYED_STATES = ['EXITED', 'FAILEDTOSTART', 'FINISHED', 'UNHEALTHY', 'STOPPED', 'EXIT CODE 137', 'OOMKILLED', 'RUNTIMEUNHEALTHY', 'CRASHLOOP', 'NOT FOUND'];
const SATELLITE_MESSAGES = ['SPANS INGESTED', 'TRACES COLLECTED', 'LOGS EXPORTED', 'METRICS SCRAPED'];
const GAME_OVER_MESSAGES = ['INCIDENT REPORT FILED', 'ON-CALL HAS BEEN PAGED', 'HAVE YOU TRIED ASPIRE RUN?', 'ALL HEALTH CHECKS FAILING', 'WORKS ON MY MACHINE'];
const SATELLITE_POINTS = [100, 200, 400];

// ---------------------------------------------------------------------------------------------------------------------
// Pure logic
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Lays out the enemy formation. Every resource in the graph appears at least once. Tiny graphs are repeated to fill a
 * proper formation, and later waves get bigger, up to MAX_ENEMIES.
 */
export function buildFormation(resourceCount, wave) {
    const count = Math.max(1, resourceCount);
    const minimum = Math.min(MAX_ENEMIES, 15 + ((wave - 1) * 6));
    const total = Math.min(MAX_ENEMIES, Math.max(count, minimum));
    const columns = Math.min(MAX_COLUMNS, Math.max(5, Math.ceil(Math.sqrt(total * 2))));
    const rows = Math.ceil(total / columns);

    const slots = [];
    for (let i = 0; i < total; i++) {
        const row = Math.floor(i / columns);
        // A partial last row is centered under the others.
        const inRow = row === rows - 1 ? total - (row * columns) : columns;
        const col = (i % columns) + ((columns - inRow) / 2);
        slots.push({ resourceIndex: i % count, row, col });
    }

    return { columns, rows, slots };
}

/**
 * Sizes the virtual playfield for a graph of the given size, in CSS pixels. Small or very wide and short graphs get
 * smaller pixels rather than a playfield the formation doesn't fit in. Both axes share one scale so the playfield fills
 * the graph without distorting the sprites.
 */
export function computeWorldSize(rectWidth, rectHeight) {
    const preferred = clamp(Math.round(rectWidth / 420), 2, 4);
    const pixelScale = Math.min(preferred, rectWidth / MIN_WORLD_WIDTH, rectHeight / MIN_WORLD_HEIGHT) || 1;
    return {
        width: Math.max(MIN_WORLD_WIDTH, Math.floor(rectWidth / pixelScale)),
        height: Math.max(MIN_WORLD_HEIGHT, Math.floor(rectHeight / pixelScale)),
    };
}

/**
 * Whether a key press on the focused graph starts the game. Keys typed in a control inside the graph, such as the zoom
 * buttons, stay with that control.
 */
export function isLaunchKey(event) {
    return (event.key === 'o' || event.key === 'O') &&
        !event.repeat && !event.ctrlKey && !event.metaKey && !event.altKey &&
        !event.target?.closest?.('a, button, fluent-button, [role="button"], input, textarea, select, [contenteditable=""], [contenteditable="true"]');
}

/**
 * States the game can be paused in. The ones between two lives or two waves count: the game would otherwise carry on from
 * them on its own, and cost lives while the user is working elsewhere in the dashboard.
 */
export function isPausable(state) {
    return state === 'playing' || state === 'ready' || state === 'dying' || state === 'cleared';
}

/** Whether Enter and Space on this target should activate a focused button instead of playing the game. */
export function isButtonTarget(target) {
    return typeof target?.closest === 'function' && target.closest('button') !== null;
}

/**
 * Applies hard core mode to the real resources. A resource is stopped once however many of its enemies are destroyed, and
 * only resources this class stopped are started again, so a resource that was already stopped stays as the user left it.
 *
 * Commands for one resource run one after the other. A command that isn't available yet is tried again, because it can
 * take a moment for a resource to get there: start only appears once the resource has stopped, and stop only once a
 * resource that was starting is running. execute(id, command) resolves to null when the command isn't available, and
 * otherwise to whether it succeeded. A command that failed or that the user cancelled isn't tried again, and the dashboard
 * has already reported it. A resource that failed to start counts as down, and is tried again when the next wave is cleared.
 */
export class ResourceOutage {
    constructor(execute, onChange, retryDelayMs = COMMAND_RETRY_MS) {
        this.execute = execute;
        this.onChange = onChange;
        this.retryDelayMs = retryDelayMs;
        // Stop was requested since the last cleared wave, whatever came of it: one attempt per resource per wave.
        this.attempted = new Set();
        // Stop succeeded and start hasn't, so the resource is really down because of the game.
        this.stopped = new Set();
        this.stopsCancelled = false;
        this.queues = new Map();
    }

    get downCount() {
        return this.stopped.size;
    }

    /** Returns true when this call is the one that requests the stop. onStopped runs if the stop succeeds. */
    stop(id, onStopped) {
        if (this.attempted.has(id)) {
            return false;
        }
        this.attempted.add(id);
        this.enqueue(id, async () => {
            if (await this.attempt(id, 'stop') === true) {
                this.stopped.add(id);
                onStopped?.();
            }
        });
        return true;
    }

    restartAll() {
        for (const id of new Set([...this.attempted, ...this.stopped])) {
            this.enqueue(id, async () => {
                if (!this.stopped.has(id)) {
                    return;
                }
                const started = await this.attempt(id, 'start');
                if (started === true) {
                    this.stopped.delete(id);
                } else if (started === null) {
                    // A failed command has its own toast, but nothing else reports this one. It's what happens when the
                    // user leaves the page before a queued start has run, because its interop reference is then disposed.
                    console.warn(`Aspire Outage couldn't start resource '${id}'. It stays stopped.`);
                }
            });
        }
        this.forgetAttempts();
    }

    /**
     * A new game gets its own attempts. Resources left down by the last game stay tracked, so clearing a wave restarts
     * them too, and so do commands that are still queued.
     */
    forgetAttempts() {
        this.attempted.clear();
    }

    /**
     * The game is over for good: a stop that is still waiting must not take effect behind the user's back, when they may
     * already be working with the resource again. Starts carry on, since they put resources back.
     */
    cancelPendingStops() {
        this.stopsCancelled = true;
    }

    enqueue(id, work) {
        // Commands can finish long after they were requested, such as on the game over screen, which shows the count.
        const next = (this.queues.get(id) ?? Promise.resolve()).then(work).then(() => this.onChange());
        this.queues.set(id, next);
    }

    async attempt(id, command) {
        for (let i = 0; i < COMMAND_ATTEMPTS; i++) {
            if (command === 'stop' && this.stopsCancelled) {
                return null;
            }
            try {
                const result = await this.execute(id, command);
                if (result !== null) {
                    return result;
                }
            } catch {
                // The page can be gone by now, which ends the attempts below.
            }
            if (i < COMMAND_ATTEMPTS - 1) {
                await new Promise(resolve => setTimeout(resolve, this.retryDelayMs));
            }
        }
        return null;
    }

    /** Resolves when every command requested so far has finished. */
    whenIdle() {
        return Promise.all(this.queues.values());
    }
}

/** The top third of the formation is worth 40 points, the middle 25 and the bottom 15. */
export function enemyPoints(row, rows) {
    const tier = rows <= 1 ? 0 : row / (rows - 1);
    if (tier < 0.34) {
        return 40;
    }
    return tier < 0.67 ? 25 : 15;
}

/** Seconds between two formation steps. Steps get faster as enemies are destroyed and with every wave. */
export function marchInterval(alive, total, wave) {
    const base = Math.max(0.28, 0.8 * Math.pow(0.86, wave - 1));
    const fraction = total > 0 ? alive / total : 0;
    return Math.max(0.03, base * (0.04 + (0.96 * fraction)));
}

/** Number of extra lives earned by going from previousScore to score. */
export function extraLives(previousScore, score) {
    return Math.floor(score / EXTRA_LIFE_SCORE) - Math.floor(previousScore / EXTRA_LIFE_SCORE);
}

export function rectsOverlap(a, b) {
    return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

// Firewall silhouette, 20x10 cells: a wall with crenellations on top.
const FIREWALL_ROWS = [
    '###  ####  ####  ###',
    '####################',
    '####################',
    '####################',
    '####################',
    '####################',
    '####################',
    '####################',
    '####################',
    '####################',
];

export function createFirewall(x, y) {
    const h = FIREWALL_ROWS.length;
    const w = FIREWALL_ROWS[0].length;
    const cells = new Uint8Array(w * h);
    for (let row = 0; row < h; row++) {
        for (let col = 0; col < w; col++) {
            cells[(row * w) + col] = FIREWALL_ROWS[row][col] === '#' ? 1 : 0;
        }
    }
    return { x, y, w, h, cells, dirty: true, canvas: null };
}

/** Returns the world position of the first solid firewall cell inside rect, or null. */
export function firewallCollision(firewall, rect) {
    const left = Math.max(0, Math.floor(rect.x - firewall.x));
    const right = Math.min(firewall.w - 1, Math.ceil(rect.x + rect.w - firewall.x) - 1);
    const top = Math.max(0, Math.floor(rect.y - firewall.y));
    const bottom = Math.min(firewall.h - 1, Math.ceil(rect.y + rect.h - firewall.y) - 1);
    for (let row = top; row <= bottom; row++) {
        for (let col = left; col <= right; col++) {
            if (firewall.cells[(row * firewall.w) + col]) {
                return { x: firewall.x + col, y: firewall.y + row };
            }
        }
    }
    return null;
}

/** Erodes a ragged hole around a world position. Returns the number of cells removed. */
export function damageFirewall(firewall, x, y, radius, random = Math.random) {
    let removed = 0;
    const r = Math.ceil(radius);
    for (let dy = -r; dy <= r; dy++) {
        for (let dx = -r; dx <= r; dx++) {
            const col = Math.round(x - firewall.x) + dx;
            const row = Math.round(y - firewall.y) + dy;
            if (col < 0 || row < 0 || col >= firewall.w || row >= firewall.h) {
                continue;
            }
            const distance = Math.hypot(dx, dy);
            // The rim of the hole is dithered so it looks chewed rather than stamped.
            if (distance <= radius - 1 || (distance <= radius && random() < 0.5)) {
                const index = (row * firewall.w) + col;
                if (firewall.cells[index]) {
                    firewall.cells[index] = 0;
                    removed++;
                }
            }
        }
    }
    if (removed > 0) {
        firewall.dirty = true;
    }
    return removed;
}

/** Erases every firewall cell under rect, used when the formation moves through a firewall. */
export function eraseFirewall(firewall, rect) {
    let removed = 0;
    for (let row = 0; row < firewall.h; row++) {
        for (let col = 0; col < firewall.w; col++) {
            const index = (row * firewall.w) + col;
            if (firewall.cells[index] && rectsOverlap(rect, { x: firewall.x + col, y: firewall.y + row, w: 1, h: 1 })) {
                firewall.cells[index] = 0;
                removed++;
            }
        }
    }
    if (removed > 0) {
        firewall.dirty = true;
    }
    return removed;
}

// Storage can be unavailable (private windows, blocked site data) or throw on access, so every access is guarded and
// the game works without it.
export function readBestScore(getStorage) {
    try {
        const value = Number.parseInt(getStorage()?.getItem(BEST_SCORE_KEY) ?? '', 10);
        return Number.isFinite(value) && value > 0 ? value : 0;
    } catch {
        return 0;
    }
}

export function saveBestScore(getStorage, score) {
    const best = readBestScore(getStorage);
    if (score <= best) {
        return best;
    }
    try {
        getStorage()?.setItem(BEST_SCORE_KEY, String(score));
    } catch {
        // The best score is a nicety; ignore storage failures.
    }
    return score;
}

function readMuted(getStorage) {
    try {
        return getStorage()?.getItem(MUTED_KEY) === 'true';
    } catch {
        return false;
    }
}

function saveMuted(getStorage, muted) {
    try {
        getStorage()?.setItem(MUTED_KEY, muted ? 'true' : 'false');
    } catch {
        // Ignore storage failures.
    }
}

export function padScore(score) {
    return String(Math.max(0, Math.floor(score))).padStart(6, '0');
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function easeInOutCubic(t) {
    return t < 0.5 ? 4 * t * t * t : 1 - (Math.pow(-2 * t + 2, 3) / 2);
}

function lerp(a, b, t) {
    return a + ((b - a) * t);
}

function pick(values) {
    return values[Math.floor(Math.random() * values.length)];
}

// ---------------------------------------------------------------------------------------------------------------------
// Pixel font (5x7 glyphs, 1 pixel spacing). Lowercase is drawn as uppercase; unknown characters as '?'.
// ---------------------------------------------------------------------------------------------------------------------

const FONT_SOURCE = {
    'A': ' ### |#   #|#   #|#####|#   #|#   #|#   #',
    'B': '#### |#   #|#   #|#### |#   #|#   #|#### ',
    'C': ' ### |#   #|#    |#    |#    |#   #| ### ',
    'D': '#### |#   #|#   #|#   #|#   #|#   #|#### ',
    'E': '#####|#    |#    |#### |#    |#    |#####',
    'F': '#####|#    |#    |#### |#    |#    |#    ',
    'G': ' ### |#   #|#    |# ###|#   #|#   #| ####',
    'H': '#   #|#   #|#   #|#####|#   #|#   #|#   #',
    'I': ' ### |  #  |  #  |  #  |  #  |  #  | ### ',
    'J': '  ###|   # |   # |   # |   # |#  # | ##  ',
    'K': '#   #|#  # |# #  |##   |# #  |#  # |#   #',
    'L': '#    |#    |#    |#    |#    |#    |#####',
    'M': '#   #|## ##|# # #|# # #|#   #|#   #|#   #',
    'N': '#   #|#   #|##  #|# # #|#  ##|#   #|#   #',
    'O': ' ### |#   #|#   #|#   #|#   #|#   #| ### ',
    'P': '#### |#   #|#   #|#### |#    |#    |#    ',
    'Q': ' ### |#   #|#   #|#   #|# # #|#  # | ## #',
    'R': '#### |#   #|#   #|#### |# #  |#  # |#   #',
    'S': ' ####|#    |#    | ### |    #|    #|#### ',
    'T': '#####|  #  |  #  |  #  |  #  |  #  |  #  ',
    'U': '#   #|#   #|#   #|#   #|#   #|#   #| ### ',
    'V': '#   #|#   #|#   #|#   #|#   #| # # |  #  ',
    'W': '#   #|#   #|#   #|# # #|# # #|# # #| # # ',
    'X': '#   #|#   #| # # |  #  | # # |#   #|#   #',
    'Y': '#   #|#   #| # # |  #  |  #  |  #  |  #  ',
    'Z': '#####|    #|   # |  #  | #   |#    |#####',
    '0': ' ### |#   #|#  ##|# # #|##  #|#   #| ### ',
    '1': '  #  | ##  |  #  |  #  |  #  |  #  | ### ',
    '2': ' ### |#   #|    #|   # |  #  | #   |#####',
    '3': '#####|   # |  #  |   # |    #|#   #| ### ',
    '4': '   # |  ## | # # |#  # |#####|   # |   # ',
    '5': '#####|#    |#### |    #|    #|#   #| ### ',
    '6': '  ## | #   |#    |#### |#   #|#   #| ### ',
    '7': '#####|    #|   # |  #  | #   | #   | #   ',
    '8': ' ### |#   #|#   #| ### |#   #|#   #| ### ',
    '9': ' ### |#   #|#   #| ####|    #|   # | ##  ',
    ' ': '     |     |     |     |     |     |     ',
    '-': '     |     |     |#####|     |     |     ',
    '.': '     |     |     |     |     | ##  | ##  ',
    ',': '     |     |     |     | ##  |  #  | #   ',
    ':': '     | ##  | ##  |     | ##  | ##  |     ',
    '!': '  #  |  #  |  #  |  #  |  #  |     |  #  ',
    '?': ' ### |#   #|    #|   # |  #  |     |  #  ',
    '+': '     |  #  |  #  |#####|  #  |  #  |     ',
    '_': '     |     |     |     |     |     |#####',
    '/': '    #|    #|   # |  #  | #   |#    |#    ',
    '(': '   # |  #  | #   | #   | #   |  #  |   # ',
    ')': ' #   |  #  |   # |   # |   # |  #  | #   ',
    '=': '     |     |#####|     |#####|     |     ',
    '<': '   # |  #  | #   |#    | #   |  #  |   # ',
    '>': ' #   |  #  |   # |    #|   # |  #  | #   ',
    '\'': '  #  |  #  |     |     |     |     |     ',
    '*': '     |# # #| ### |#####| ### |# # #|     ',
};

const FONT = parseFont(FONT_SOURCE);

function parseFont(source) {
    const font = new Map();
    for (const [ch, rows] of Object.entries(source)) {
        const pixels = [];
        rows.split('|').forEach((row, y) => {
            for (let x = 0; x < 5; x++) {
                if (row[x] === '#') {
                    pixels.push(x, y);
                }
            }
        });
        font.set(ch, pixels);
    }
    return font;
}

export function textWidth(text, scale = 1) {
    return text.length > 0 ? ((text.length * 6) - 1) * scale : 0;
}

function drawText(ctx, text, x, y, color, scale = 1, align = 'left') {
    const value = String(text).toUpperCase();
    const width = textWidth(value, scale);
    let cx = Math.round(align === 'center' ? x - (width / 2) : align === 'right' ? x - width : x);
    ctx.fillStyle = color;
    for (const ch of value) {
        const pixels = FONT.get(ch) ?? FONT.get('?');
        for (let i = 0; i < pixels.length; i += 2) {
            ctx.fillRect(cx + (pixels[i] * scale), y + (pixels[i + 1] * scale), scale, scale);
        }
        cx += 6 * scale;
    }
}

function shorten(text, maxLength) {
    return text.length > maxLength ? text.slice(0, maxLength - 1) + '.' : text;
}

// ---------------------------------------------------------------------------------------------------------------------
// Sprites
// ---------------------------------------------------------------------------------------------------------------------

// Paths from Components/Controls/AspireLogo.razor (viewBox 0 0 256 256).
const ASPIRE_LOGO = [
    { color: '#512BD4', d: 'M28 240C12.56 240 0 227.44 0 212C0 206.968 1.328 202.072 3.84 197.832L70.544 82.296L70.816 81.888L103.744 24.792C108.744 16.152 118.032 10.792 128 10.792C137.968 10.792 147.256 16.152 152.248 24.784L252.248 197.992C254.664 202.064 255.992 206.96 255.992 211.992C255.992 227.432 243.432 239.992 227.992 239.992L28 240Z' },
    { color: '#7455DD', d: 'M202.64 144H135.92L128 130.24L105.04 90.48C104 88.72 102.56 87.2 100.64 86.16C94.96 82.8 87.6 84.8 84.24 90.56L117.6 32.8C119.68 29.2 123.52 26.8 128 26.8C132.48 26.8 136.32 29.2 138.4 32.8L171.6 90.32L171.68 90.48L171.84 90.72L202.64 144Z' },
    { color: '#9780E5', d: 'M240 212C240 218.64 234.64 224 228 224H161.36C168 224 173.36 218.64 173.36 212C173.36 209.84 172.72 207.76 171.76 206L138.4 148.24L135.92 144H202.64L238.4 206C239.44 207.76 240 209.84 240 212Z' },
    { color: '#B9AAEE', d: 'M173.36 212C173.36 218.64 168 224 161.36 224H94.6399C101.28 224 106.64 218.64 106.64 212C106.64 209.84 106.08 207.76 105.04 206C105.04 205.92 104.96 205.84 104.88 205.76L94.3199 188.56L70.5599 149.76C68.3999 146.24 64.5599 144 60.3199 144H135.92L138.4 148.24L139.416 150L171.76 206C172.72 207.76 173.36 209.84 173.36 212Z' },
    { color: '#DCD5F6', d: 'M106.64 212C106.64 218.64 101.28 224 94.64 224H28C21.36 224 16 218.64 16 212C16 209.84 16.56 207.76 17.6 206L49.92 150C52.08 146.32 56.08 144 60.32 144C64.56 144 68.4 146.24 70.56 149.76L94.32 188.56L104.88 205.76C104.96 205.84 105.04 205.92 105.04 206C106.08 207.76 106.64 209.84 106.64 212Z' },
    { color: '#9780E5', d: 'M135.92 144H60.32C56.08 144 52.08 146.32 49.92 150L53.36 144L83.92 91.12L84.24 90.64V90.56C87.6 84.8 94.96 82.8 100.64 86.16C102.56 87.2 104 88.72 105.04 90.48L128 130.24L135.92 144Z' },
];
const ASPIRE_LOGO_BOX = { x: 0, y: 10.792, w: 256, h: 229.2 };

// Fallback enemy when a resource has no usable icon: a container box.
const FALLBACK_ENEMY_ROWS = [
    ' ######### ',
    ' # # # # # ',
    ' ######### ',
    ' #       # ',
    ' # ##### # ',
    ' #       # ',
    ' ######### ',
    '  #     #  ',
];

// The bonus target represents OTLP telemetry flying over the app: a satellite with two solar panels.
const SATELLITE_ROWS = [
    '       ##       ',
    '      ####      ',
    '### # #### # ###',
    '################',
    '### # #### # ###',
    '      ####      ',
    '       ##       ',
];

const SPARK_ROWS = [
    '    #    ',
    ' #  #  # ',
    '  #   #  ',
    '         ',
    '##     ##',
    '         ',
    '  #   #  ',
    ' #  #  # ',
    '    #    ',
];

// Two-frame legs drawn under each icon so resource icons appear to walk. Offsets are [x, y below the icon].
const LEGS = [
    [[2, 0], [1, 1], [9, 0], [10, 1]],
    [[2, 0], [3, 1], [9, 0], [8, 1]],
];

const BOMB_FRAMES = [
    [[1, 0], [0, 1], [1, 2], [2, 3], [1, 4], [0, 5], [1, 6]],
    [[1, 0], [2, 1], [1, 2], [0, 3], [1, 4], [2, 5], [1, 6]],
];

function createCanvas(width, height) {
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    return canvas;
}

function bitmapSprite(rows, color) {
    const height = rows.length;
    const width = rows[0].length;
    const canvas = createCanvas(width, height);
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = color;
    const pixels = [];
    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
            if (rows[y][x] === '#') {
                ctx.fillRect(x, y, 1, 1);
                pixels.push({ x, y, color });
            }
        }
    }
    return { canvas, width, height, pixels };
}

function pathSprite(paths, width, height, box) {
    const canvas = createCanvas(width, height);
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    const scale = Math.min(width / box.w, height / box.h);
    const offsetX = ((width - (box.w * scale)) / 2) - (box.x * scale);
    const offsetY = ((height - (box.h * scale)) / 2) - (box.y * scale);
    ctx.setTransform(scale, 0, 0, scale, offsetX, offsetY);
    for (const path of paths) {
        ctx.fillStyle = path.color;
        ctx.fill(new Path2D(path.d));
    }
    ctx.setTransform(1, 0, 0, 1, 0, 0);

    // Snap anti-aliased edges to fully opaque or transparent pixels so the sprite reads as hard pixel art.
    const image = ctx.getImageData(0, 0, width, height);
    const data = image.data;
    const pixels = [];
    for (let i = 0; i < width * height; i++) {
        const offset = i * 4;
        if (data[offset + 3] >= 96) {
            data[offset + 3] = 255;
            pixels.push({ x: i % width, y: Math.floor(i / width), color: `rgb(${data[offset]},${data[offset + 1]},${data[offset + 2]})` });
        } else {
            data[offset + 3] = 0;
        }
    }
    ctx.putImageData(image, 0, 0);
    return { canvas, width, height, pixels };
}

function usableColor(value, fallback) {
    return typeof value === 'string' && (value.startsWith('rgb') || value.startsWith('#')) ? value : fallback;
}

/** Reads the graph's rendered nodes: identity, label, icon sprite, color and on-screen position. */
function collectRoster(container) {
    const roster = [];
    for (const group of container.querySelectorAll('.resource-group')) {
        const data = group.__data__;
        const circle = group.querySelector('.resource-node');
        if (!data || !circle) {
            continue;
        }
        const rect = circle.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            continue;
        }

        const circleStyle = getComputedStyle(circle);
        const iconPath = group.querySelector('.resource-scale > g:not([class]) > path');
        let color = '#b9aaee';
        let sprite = null;
        if (iconPath) {
            color = usableColor(getComputedStyle(iconPath).fill, color);
            const d = iconPath.getAttribute('d');
            const box = d ? iconPath.getBBox() : null;
            if (box && box.width > 0 && box.height > 0) {
                sprite = pathSprite([{ d, color }], SPRITE, SPRITE, { x: box.x, y: box.y, w: box.width, h: box.height });
            }
        }
        if (!sprite || sprite.pixels.length < 6) {
            sprite = bitmapSprite(FALLBACK_ENEMY_ROWS, color);
        }

        roster.push({
            id: data.id,
            label: String(data.label || data.id),
            color,
            fill: usableColor(circleStyle.fill, '#1b1a22'),
            sprite,
            screen: { x: rect.left + (rect.width / 2), y: rect.top + (rect.height / 2), r: rect.width / 2 },
        });
    }
    return roster;
}

// ---------------------------------------------------------------------------------------------------------------------
// Sound: everything is synthesized with the Web Audio API, nothing is downloaded.
// ---------------------------------------------------------------------------------------------------------------------

class SoundEffects {
    constructor(muted) {
        this.muted = muted;
        this.context = null;
        this.satelliteNodes = null;
        this.marchIndex = 0;
    }

    // Browsers only allow audio to start from a user activation (click, key press). The click or key press that launches
    // the game counts, and later ones resume a context the browser suspended.
    unlock() {
        if (!this.context) {
            const AudioContextType = window.AudioContext ?? window.webkitAudioContext;
            if (!AudioContextType) {
                return;
            }
            try {
                this.context = new AudioContextType();
            } catch {
                return;
            }
            this.master = this.context.createGain();
            this.master.gain.value = this.muted ? 0 : 0.22;
            this.master.connect(this.context.destination);

            const length = Math.floor(this.context.sampleRate);
            this.noiseBuffer = this.context.createBuffer(1, length, this.context.sampleRate);
            const channel = this.noiseBuffer.getChannelData(0);
            for (let i = 0; i < length; i++) {
                channel[i] = (Math.random() * 2) - 1;
            }
        }
        if (this.context.state === 'suspended') {
            this.context.resume().catch(() => { });
        }
    }

    setMuted(muted) {
        this.muted = muted;
        if (this.context) {
            this.master.gain.setTargetAtTime(muted ? 0 : 0.22, this.context.currentTime, 0.01);
        }
    }

    get playable() {
        return this.context !== null && !this.muted;
    }

    tone(type, from, to, duration, volume, delay = 0) {
        if (!this.playable) {
            return;
        }
        const ctx = this.context;
        const t0 = ctx.currentTime + delay;
        const oscillator = ctx.createOscillator();
        oscillator.type = type;
        oscillator.frequency.setValueAtTime(from, t0);
        if (to !== from) {
            oscillator.frequency.exponentialRampToValueAtTime(Math.max(1, to), t0 + duration);
        }
        const gain = ctx.createGain();
        gain.gain.setValueAtTime(volume, t0);
        gain.gain.exponentialRampToValueAtTime(0.001, t0 + duration);
        oscillator.connect(gain).connect(this.master);
        oscillator.start(t0);
        oscillator.stop(t0 + duration + 0.02);
    }

    noise(duration, volume, frequency, delay = 0) {
        if (!this.playable) {
            return;
        }
        const ctx = this.context;
        const t0 = ctx.currentTime + delay;
        const source = ctx.createBufferSource();
        source.buffer = this.noiseBuffer;
        source.loop = true;
        const filter = ctx.createBiquadFilter();
        filter.type = 'lowpass';
        filter.frequency.setValueAtTime(frequency, t0);
        filter.frequency.exponentialRampToValueAtTime(80, t0 + duration);
        const gain = ctx.createGain();
        gain.gain.setValueAtTime(volume, t0);
        gain.gain.exponentialRampToValueAtTime(0.001, t0 + duration);
        source.connect(filter).connect(gain).connect(this.master);
        source.start(t0);
        source.stop(t0 + duration + 0.02);
    }

    arpeggio(notes, spacing, duration, volume, type = 'square', delay = 0) {
        notes.forEach((note, i) => this.tone(type, note, note * 0.99, duration, volume, delay + (i * spacing)));
    }

    shoot() {
        this.tone('square', 1400, 220, 0.11, 0.16);
    }

    enemyExplosion() {
        this.noise(0.28, 0.5, 2400);
        this.tone('square', 300, 60, 0.18, 0.12);
    }

    // A rising three-note blip, like a monitor's tick. Its tempo is the formation's step rate.
    marchBeat() {
        const notes = [196, 247, 294];
        const note = notes[this.marchIndex++ % notes.length];
        this.tone('triangle', note, note * 1.02, 0.06, 0.18);
    }

    // The satellite sends short telemetry pings while it crosses the screen.
    satelliteStart() {
        if (!this.context || this.satelliteNodes) {
            return;
        }
        const telemetryPing = () => this.tone('sine', 1250, 1750, 0.05, 0.1);
        telemetryPing();
        this.satelliteNodes = { timer: setInterval(telemetryPing, 420) };
    }

    satelliteStop() {
        if (!this.satelliteNodes) {
            return;
        }
        clearInterval(this.satelliteNodes.timer);
        this.satelliteNodes = null;
    }

    satelliteHit() {
        for (let i = 0; i < 4; i++) {
            this.tone('triangle', 1600, 400, 0.1, 0.25, i * 0.08);
        }
    }

    playerHit() {
        this.noise(1.1, 0.7, 1200);
        this.tone('sawtooth', 440, 40, 1.0, 0.2);
    }

    start() {
        this.arpeggio([131, 196, 262, 392, 523], 0.07, 0.1, 0.18);
    }

    waveClear() {
        this.arpeggio([262, 330, 392, 523, 659, 784], 0.08, 0.12, 0.18);
    }

    extraLife() {
        this.arpeggio([659, 880, 1319], 0.07, 0.1, 0.18, 'triangle');
    }

    gameOver() {
        this.arpeggio([392, 330, 262, 196, 131], 0.2, 0.22, 0.22, 'square', 0.3);
    }

    powerDown() {
        this.tone('sawtooth', 600, 40, 0.6, 0.15);
    }

    dispose() {
        this.satelliteStop();
        if (this.context) {
            const context = this.context;
            this.context = null;
            // Let the power-down sound finish before releasing the audio device.
            setTimeout(() => context.close().catch(() => { }), 800);
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Styles. The dashboard's CSP allows inline styles ('unsafe-inline' in style-src), so they are injected on demand and
// removed when the game closes.
// ---------------------------------------------------------------------------------------------------------------------

const STYLE_ID = 'resource-graph-game-style';
const STYLES = `
.resource-graph-game { position: absolute; inset: 0; z-index: 20; overflow: hidden; outline: none; cursor: crosshair; user-select: none; touch-action: none; }
.resource-graph-game canvas { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; image-rendering: crisp-edges; image-rendering: pixelated; filter: saturate(1.2) drop-shadow(0 0 2px rgba(190, 170, 255, 0.55)); }
.resource-graph-game-crt { position: absolute; inset: 0; pointer-events: none; opacity: 0; background: repeating-linear-gradient(to bottom, rgba(0, 0, 0, 0.3) 0, rgba(0, 0, 0, 0.3) 1px, transparent 1px, transparent 3px), radial-gradient(ellipse at center, transparent 55%, rgba(0, 0, 0, 0.6) 100%); }
.resource-graph-game-buttons { position: absolute; right: 8px; bottom: 6px; display: flex; gap: 6px; opacity: 0; }
.resource-graph-game-button { font: 700 11px/1 ui-monospace, Menlo, Consolas, monospace; letter-spacing: 1px; text-transform: uppercase; color: #e8e2ff; background: rgba(20, 10, 40, 0.85); border: 2px solid #7455dd; border-radius: 0; padding: 4px 8px; cursor: pointer; }
.resource-graph-game-button:hover, .resource-graph-game-button:focus-visible { background: #512bd4; outline: none; }
.resource-graph-game-modes { position: absolute; left: 8px; right: 8px; bottom: 32px; display: flex; flex-direction: column; align-items: center; gap: 6px; pointer-events: none; }
.resource-graph-game-modes .resource-graph-game-button { pointer-events: auto; }
.resource-graph-game-modes[hidden], .resource-graph-game-note[hidden] { display: none; }
.resource-graph-game-mode-buttons { display: flex; gap: 6px; }
.resource-graph-game-modes .resource-graph-game-button[aria-pressed="true"] { background: #512bd4; }
.resource-graph-game-modes .resource-graph-game-hardcore[aria-pressed="true"] { background: #a4262c; border-color: #ff5f5f; }
.resource-graph-game-note { max-width: 420px; padding: 4px 8px; font: 700 11px/1.35 ui-monospace, Menlo, Consolas, monospace; text-align: center; color: #ffd9d9; background: rgba(20, 10, 40, 0.92); border: 2px solid #ff5f5f; }
`;

function injectStyles() {
    if (!document.getElementById(STYLE_ID)) {
        const style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = STYLES;
        document.head.appendChild(style);
    }
}

function removeStyles() {
    document.getElementById(STYLE_ID)?.remove();
}

function getLocalStorage() {
    return window.localStorage;
}

function isShown(element) {
    return element.isConnected && element.getClientRects().length > 0;
}

function restoreStyle(element, style) {
    if (style === null) {
        element.removeAttribute('style');
    } else {
        element.setAttribute('style', style);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Game
// ---------------------------------------------------------------------------------------------------------------------

let activeGame = null;
const attachedGraphs = new WeakSet();

class ResourceGraphGame {
    constructor(graph, svg, container, opener, strings, onClosed) {
        this.graph = graph;
        this.opener = opener;
        this.strings = strings;
        this.svg = svg;
        this.container = container;
        this.onClosed = onClosed;
        this.reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
        this.closed = false;

        const rect = container.getBoundingClientRect();
        const world = computeWorldSize(rect.width, rect.height);
        this.width = world.width;
        this.height = world.height;

        // Read the graph before hiding it: positions, icons and colors come from the rendered nodes.
        this.roster = collectRoster(container);
        this.createOverlay();
        if (this.roster.length === 0) {
            this.roster.push({
                id: 'aspire',
                placeholder: true,
                label: 'NO RESOURCES',
                color: '#b9aaee',
                fill: '#1b1a22',
                sprite: bitmapSprite(FALLBACK_ENEMY_ROWS, '#b9aaee'),
                screen: { x: rect.left + (rect.width / 2), y: rect.top + (rect.height / 2), r: 30 },
            });
        }
        for (const entry of this.roster) {
            entry.from = this.screenToWorld(entry.screen.x, entry.screen.y, entry.screen.r);
        }
        const indexById = new Map(this.roster.map((entry, index) => [entry.id, index]));
        this.links = (graph.links ?? [])
            .map(link => [indexById.get(link.source?.id ?? link.source), indexById.get(link.target?.id ?? link.target)])
            .filter(([a, b]) => a !== undefined && b !== undefined);

        this.hideGraph();

        this.sound = new SoundEffects(readMuted(getLocalStorage));
        this.sound.unlock();
        this.updateMuteButton();
        this.hardcore = false;
        this.outage = new ResourceOutage(
            (id, command) => graph.resourcesInterop.invokeMethodAsync('ExecuteGraphGameCommand', id, command),
            () => this.updateModeControls());
        this.best = readBestScore(getLocalStorage);
        this.ship = pathSprite(ASPIRE_LOGO, 17, 15, ASPIRE_LOGO_BOX);
        this.lifeIcon = pathSprite(ASPIRE_LOGO, 8, 7, ASPIRE_LOGO_BOX);
        this.satelliteSprite = bitmapSprite(SATELLITE_ROWS, '#ff5fd2');
        this.sparkSprite = bitmapSprite(SPARK_ROWS, '#ffffff');
        this.stars = Array.from({ length: 60 }, () => ({ x: Math.random() * this.width, y: Math.random() * this.height, phase: Math.random() * TAU }));

        this.keys = { left: false, right: false, fire: false };
        this.pointerX = null;
        this.pointerFire = false;
        this.particles = [];
        this.texts = [];
        this.flashes = [];
        this.shake = 0;
        this.clock = 0;

        this.resetGame();
        this.state = 'morph-in';
        this.stateTime = 0;
        this.morphInDuration = this.reducedMotion
            ? REDUCED_MORPH
            : MORPH_PIXELATE + (Math.min(this.enemies.length, 30) * MORPH_STAGGER) + MORPH_FLIGHT;

        this.addListeners();
        this.root.focus({ preventScroll: true });
        this.lastFrame = performance.now();
        this.frameHandle = requestAnimationFrame(this.frame);
    }

    // --- DOM ---------------------------------------------------------------------------------------------------------

    createOverlay() {
        injectStyles();
        const root = document.createElement('div');
        root.className = 'resource-graph-game';
        root.tabIndex = -1;
        root.setAttribute('role', 'application');
        root.setAttribute('aria-label', this.strings.instructions);

        const canvas = createCanvas(this.width, this.height);
        const crt = document.createElement('div');
        crt.className = 'resource-graph-game-crt';

        const buttons = document.createElement('div');
        buttons.className = 'resource-graph-game-buttons';
        const mute = document.createElement('button');
        mute.type = 'button';
        mute.className = 'resource-graph-game-button';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'resource-graph-game-button';
        close.textContent = this.strings.exit;
        close.setAttribute('aria-label', this.strings.exitLabel);
        buttons.append(mute, close);

        // DOM rather than canvas text, because the canvas font can't draw every language and picking a mode that stops
        // real resources has to be understood. Shown on the start and game over screens only.
        const modes = document.createElement('div');
        modes.className = 'resource-graph-game-modes';
        modes.hidden = true;
        const note = document.createElement('div');
        note.className = 'resource-graph-game-note';
        note.setAttribute('aria-live', 'polite');
        note.hidden = true;
        const modeButtons = document.createElement('div');
        modeButtons.className = 'resource-graph-game-mode-buttons';
        const normal = document.createElement('button');
        normal.type = 'button';
        normal.className = 'resource-graph-game-button';
        normal.textContent = this.strings.modeNormal;
        const hardcore = document.createElement('button');
        hardcore.type = 'button';
        hardcore.className = 'resource-graph-game-button resource-graph-game-hardcore';
        hardcore.textContent = this.strings.modeHardCore;
        modeButtons.append(normal, hardcore);
        modes.append(note, modeButtons);

        root.append(canvas, crt, modes, buttons);
        this.container.appendChild(root);

        this.root = root;
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;
        this.crt = crt;
        this.buttons = buttons;
        this.muteButton = mute;
        this.closeButton = close;
        this.modes = modes;
        this.modeNote = note;
        this.normalButton = normal;
        this.hardcoreButton = hardcore;
    }

    hideGraph() {
        this.controls = [...this.container.querySelectorAll('.resource-graph-controls')];
        // The whole style attribute is saved (null when absent) so exiting leaves the elements exactly as they were.
        this.saved = {
            svgStyle: this.svg.getAttribute('style'),
            controlsStyles: this.controls.map(controls => controls.getAttribute('style')),
        };
        this.svg.style.transition = 'none';
        this.svg.style.opacity = '0';
        // Opacity alone would leave the resource menu cogs focusable and exposed to assistive technology behind the game.
        // Hidden elements keep their layout, which is what morphing back to the nodes needs.
        this.svg.style.visibility = 'hidden';
        for (const controls of this.controls) {
            controls.style.visibility = 'hidden';
        }
    }

    revealGraph(animate) {
        if (this.revealed) {
            return;
        }
        this.revealed = true;
        if (animate) {
            this.svg.style.transition = 'opacity 200ms ease-out';
            this.svg.style.visibility = 'visible';
            this.svg.style.opacity = '1';
        } else {
            restoreStyle(this.svg, this.saved.svgStyle);
        }
        this.controls.forEach((controls, i) => restoreStyle(controls, this.saved.controlsStyles[i]));
    }

    updateModeControls() {
        const selecting = this.state === 'title' || this.state === 'gameover';
        this.modes.hidden = !selecting;
        this.normalButton.setAttribute('aria-pressed', this.hardcore ? 'false' : 'true');
        this.hardcoreButton.setAttribute('aria-pressed', this.hardcore ? 'true' : 'false');

        let note = '';
        if (this.state === 'gameover' && this.outage.downCount > 0) {
            note = this.strings.hardCoreStopped.replace('{0}', String(this.outage.downCount));
        } else if (selecting && this.hardcore) {
            note = this.strings.hardCoreWarning;
        }
        // The note is a live region: setting the same text again could announce it again.
        if (this.modeNote.textContent !== note) {
            this.modeNote.textContent = note;
        }
        this.modeNote.hidden = note === '';
    }

    setHardcore(hardcore) {
        this.hardcore = hardcore;
        this.updateModeControls();
    }

    updateMuteButton() {
        this.muteButton.textContent = this.sound.muted ? this.strings.soundOff : this.strings.soundOn;
        this.muteButton.setAttribute('aria-pressed', this.sound.muted ? 'true' : 'false');
    }

    screenToWorld(clientX, clientY, radius = 0) {
        const rect = this.canvas.getBoundingClientRect();
        const scale = Math.min(rect.width / this.width, rect.height / this.height) || 1;
        const offsetX = (rect.width - (this.width * scale)) / 2;
        const offsetY = (rect.height - (this.height * scale)) / 2;
        return {
            x: (clientX - rect.left - offsetX) / scale,
            y: (clientY - rect.top - offsetY) / scale,
            r: radius / scale,
        };
    }

    // --- Listeners ---------------------------------------------------------------------------------------------------

    addListeners() {
        // On the overlay rather than the window: keys belong to the game only while it has focus, so the rest of the
        // dashboard (search boxes, the resource list, its keyboard shortcuts) keeps working when the user clicks away.
        this.root.addEventListener('keydown', this.onKeyDown);
        this.root.addEventListener('keyup', this.onKeyUp);
        window.addEventListener('blur', this.onBlur);
        document.addEventListener('visibilitychange', this.onVisibilityChange);
        this.root.addEventListener('focusout', this.onFocusOut);
        this.canvas.addEventListener('pointermove', this.onPointerMove);
        this.canvas.addEventListener('pointerdown', this.onPointerDown);
        window.addEventListener('pointerup', this.onPointerUp);
        this.canvas.addEventListener('contextmenu', this.onContextMenu);
        this.muteButton.addEventListener('click', this.onMuteClick);
        this.closeButton.addEventListener('click', this.onCloseClick);
        this.normalButton.addEventListener('click', this.onModeClick);
        this.hardcoreButton.addEventListener('click', this.onModeClick);
        this.root.addEventListener('mousedown', this.onButtonMouseDown);
    }

    removeListeners() {
        this.root.removeEventListener('keydown', this.onKeyDown);
        this.root.removeEventListener('keyup', this.onKeyUp);
        window.removeEventListener('blur', this.onBlur);
        document.removeEventListener('visibilitychange', this.onVisibilityChange);
        this.root.removeEventListener('focusout', this.onFocusOut);
        this.canvas.removeEventListener('pointermove', this.onPointerMove);
        this.canvas.removeEventListener('pointerdown', this.onPointerDown);
        window.removeEventListener('pointerup', this.onPointerUp);
        this.canvas.removeEventListener('contextmenu', this.onContextMenu);
        this.muteButton.removeEventListener('click', this.onMuteClick);
        this.closeButton.removeEventListener('click', this.onCloseClick);
        this.normalButton.removeEventListener('click', this.onModeClick);
        this.hardcoreButton.removeEventListener('click', this.onModeClick);
        this.root.removeEventListener('mousedown', this.onButtonMouseDown);
    }

    onKeyDown = (event) => {
        if (this.state === 'morph-out' || event.ctrlKey || event.metaKey || event.altKey) {
            return;
        }

        switch (event.key) {
            case 'ArrowLeft':
            case 'a':
            case 'A':
                this.keys.left = true;
                this.pointerX = null;
                break;
            case 'ArrowRight':
            case 'd':
            case 'D':
                this.keys.right = true;
                this.pointerX = null;
                break;
            case ' ':
            case 'Enter':
                // Keyboard users tab to Sound and Exit: those buttons keep their native activation. Clicking them gives
                // focus back to the game, so this doesn't take Space away from firing.
                if (isButtonTarget(event.target)) {
                    return;
                }
                this.sound.unlock();
                if (!event.repeat) {
                    this.keys.fire = event.key === ' ';
                    this.primaryAction();
                }
                break;
            case 'p':
            case 'P':
                this.togglePause();
                break;
            case 'm':
            case 'M':
                this.sound.unlock();
                this.toggleMute();
                break;
            case 'h':
            case 'H':
                if (this.state === 'title' || this.state === 'gameover') {
                    this.setHardcore(!this.hardcore);
                }
                break;
            case 'Escape':
                this.quit();
                break;
            default:
                // Swallow other plain keys so single-key dashboard shortcuts don't run behind the game.
                if (event.key.length !== 1) {
                    return;
                }
                break;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
    };

    onKeyUp = (event) => {
        switch (event.key) {
            case 'ArrowLeft':
            case 'a':
            case 'A':
                this.keys.left = false;
                break;
            case 'ArrowRight':
            case 'd':
            case 'D':
                this.keys.right = false;
                break;
            case ' ':
                this.keys.fire = false;
                break;
            default:
                return;
        }
        if (!isButtonTarget(event.target)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    };

    onBlur = () => {
        this.keys.left = this.keys.right = this.keys.fire = false;
        this.pointerFire = false;
        this.pause();
    };

    onVisibilityChange = () => {
        if (document.hidden) {
            this.pause();
        }
    };

    onFocusOut = (event) => {
        if (!this.root.contains(event.relatedTarget)) {
            this.keys.left = this.keys.right = this.keys.fire = false;
            // The game no longer gets the keys, so it mustn't carry on without its player. Clicking it resumes.
            this.pause();
        }
    };

    onPointerMove = (event) => {
        this.pointerX = this.screenToWorld(event.clientX, event.clientY).x;
    };

    onPointerDown = (event) => {
        if (event.button !== 0) {
            return;
        }
        event.preventDefault();
        this.root.focus({ preventScroll: true });
        this.sound.unlock();
        this.pointerX = this.screenToWorld(event.clientX, event.clientY).x;
        this.pointerFire = true;
        this.primaryAction();
    };

    onPointerUp = () => {
        this.pointerFire = false;
    };

    onContextMenu = (event) => {
        event.preventDefault();
    };

    onMuteClick = () => {
        this.sound.unlock();
        this.toggleMute();
        this.root.focus({ preventScroll: true });
    };

    onCloseClick = () => {
        this.quit();
    };

    onModeClick = (event) => {
        this.setHardcore(event.currentTarget === this.hardcoreButton);
        this.root.focus({ preventScroll: true });
    };

    // Browsers that don't focus a clicked button would otherwise move focus out of the game, which pauses it.
    onButtonMouseDown = (event) => {
        if (isButtonTarget(event.target)) {
            event.preventDefault();
        }
    };

    toggleMute() {
        this.sound.setMuted(!this.sound.muted);
        saveMuted(getLocalStorage, this.sound.muted);
        this.updateMuteButton();
    }

    // --- State -------------------------------------------------------------------------------------------------------

    setState(state) {
        this.state = state;
        this.stateTime = 0;
        this.updateModeControls();
    }

    resetGame() {
        this.score = 0;
        this.lives = 3;
        this.wave = 1;
        this.newBest = false;
        this.outage.forgetAttempts();
        this.buildWave();
    }

    buildWave() {
        const formation = buildFormation(this.roster.length, this.wave);
        this.formation = formation;
        this.enemies = formation.slots.map(slot => ({
            resource: this.roster[slot.resourceIndex],
            row: slot.row,
            col: slot.col,
            alive: true,
            points: enemyPoints(slot.row, formation.rows),
        }));
        this.alive = this.enemies.length;

        const layoutWidth = ((formation.columns - 1) * SPACING_X) + SPRITE;
        this.baseX = Math.round((this.width - layoutWidth) / 2);
        this.baseY = HUD_HEIGHT + 14;
        this.offsetX = 0;
        // Each new wave starts a little lower.
        this.offsetY = Math.min(this.wave - 1, 5) * 4;
        this.direction = 1;
        this.legFrame = 0;
        this.stepTimer = marchInterval(this.alive, this.enemies.length, this.wave);

        this.player = { x: this.width / 2, y: this.height - 31, w: this.ship.width, h: this.ship.height, alive: true };
        this.bullet = null;
        this.bombs = [];
        this.bombTimer = 1.5;
        this.satellite = null;
        this.satelliteTimer = 12 + (Math.random() * 10);
        this.sound?.satelliteStop();

        const firewallCount = this.width >= 300 ? 4 : 3;
        const spacing = this.width / firewallCount;
        const firewallY = this.player.y - 8 - FIREWALL_ROWS.length;
        this.firewalls = Array.from({ length: firewallCount }, (_, i) =>
            createFirewall(Math.round((spacing * (i + 0.5)) - (FIREWALL_ROWS[0].length / 2)), firewallY));
    }

    enemyPosition(enemy) {
        return {
            x: Math.round(this.baseX + (enemy.col * SPACING_X) + this.offsetX),
            y: Math.round(this.baseY + (enemy.row * SPACING_Y) + this.offsetY),
        };
    }

    primaryAction() {
        switch (this.state) {
            case 'title':
                this.sound.start();
                this.setState('ready');
                break;
            case 'gameover':
                this.resetGame();
                this.sound.start();
                this.setState('ready');
                break;
            case 'paused':
                this.togglePause();
                break;
            case 'playing':
                this.fire();
                break;
        }
    }

    pause() {
        if (isPausable(this.state)) {
            this.pausedState = this.state;
            this.sound.satelliteStop();
            this.state = 'paused';
        }
    }

    togglePause() {
        if (this.state === 'paused') {
            this.state = this.pausedState;
            if (this.satellite) {
                this.sound.satelliteStart();
            }
        } else {
            this.pause();
        }
    }

    fire() {
        if (this.state !== 'playing' || this.bullet || !this.player.alive) {
            return;
        }
        this.bullet = { x: Math.round(this.player.x), y: this.player.y - 4, w: 1, h: 5 };
        this.sound.shoot();
    }

    addScore(points) {
        const previous = this.score;
        this.score += points;
        const earned = extraLives(previous, this.score);
        if (earned > 0 && this.lives < MAX_LIVES) {
            this.lives = Math.min(MAX_LIVES, this.lives + earned);
            this.addText('+1 REPLICA', this.player.x, this.player.y - 10, '#7dffa0');
            this.sound.extraLife();
        }
    }

    quit() {
        if (this.state === 'morph-out' || this.closed) {
            return;
        }
        this.outage.cancelPendingStops();
        this.recordBestScore();
        this.sound.satelliteStop();
        this.sound.powerDown();

        // Fly one enemy per resource back to where its node is now. Resources may have moved or changed while playing,
        // so node positions are read again; a resource that is gone flies off the top.
        const targets = new Map(collectRoster(this.container).map(entry => [entry.id, entry.screen]));
        const inFormation = this.state !== 'morph-in';
        this.outbound = this.roster.map(entry => {
            const enemy = this.enemies.find(a => a.resource === entry && a.alive) ?? this.enemies.find(a => a.resource === entry);
            let from = entry.from;
            if (enemy && inFormation) {
                const position = this.enemyPosition(enemy);
                from = { x: position.x + (SPRITE / 2), y: position.y + (SPRITE / 2), r: SPRITE / 2 };
            }
            const screen = targets.get(entry.id);
            const to = screen ? this.screenToWorld(screen.x, screen.y, screen.r) : { x: from.x, y: -40, r: SPRITE / 2 };
            return { entry, enemy: inFormation ? enemy : null, from, to };
        });
        this.wasInFormation = inFormation;
        this.setState('morph-out');
    }

    recordBestScore() {
        if (this.score > this.best) {
            this.best = saveBestScore(getLocalStorage, this.score);
            this.newBest = true;
        }
    }

    close() {
        if (this.closed) {
            return;
        }
        this.closed = true;
        this.outage.cancelPendingStops();
        // Leaving the page or switching layout closes the game without going through quit() or gameOver().
        this.recordBestScore();
        cancelAnimationFrame(this.frameHandle);
        this.removeListeners();
        this.sound.dispose();
        this.revealGraph(false);
        // The fade-in transition was started at least 200ms ago; putting the original style back now ends it.
        restoreStyle(this.svg, this.saved.svgStyle);
        const hadFocus = this.root.contains(document.activeElement);
        this.root.remove();
        removeStyles();
        if (hadFocus && isShown(this.container)) {
            (this.opener ?? this.container).focus({ preventScroll: true });
        }
        this.onClosed();
    }

    // --- Loop --------------------------------------------------------------------------------------------------------

    frame = (now) => {
        if (this.closed) {
            return;
        }
        // Blazor can hide (switch layout) or remove (navigate away) the graph while playing. The graph can be hidden by
        // an ancestor, so check whether it still has a box rather than looking at its own hidden attribute.
        if (!isShown(this.container) || !this.svg.isConnected) {
            this.close();
            return;
        }

        const dt = Math.min(0.05, Math.max(0, (now - this.lastFrame) / 1000));
        this.lastFrame = now;
        this.update(dt);
        if (this.closed) {
            return;
        }
        this.render();
        this.frameHandle = requestAnimationFrame(this.frame);
    };

    update(dt) {
        this.clock += dt;
        if (this.state !== 'paused') {
            this.stateTime += dt;
        }
        this.shake = Math.max(0, this.shake - dt);

        switch (this.state) {
            case 'morph-in':
                if (this.stateTime * 1000 >= this.morphInDuration) {
                    this.setState('title');
                }
                break;
            case 'morph-out':
                if (this.stateTime * 1000 >= (this.reducedMotion ? REDUCED_MORPH : MORPH_OUT) + 200) {
                    this.close();
                    return;
                }
                if (this.stateTime * 1000 >= (this.reducedMotion ? 0 : MORPH_OUT * 0.75)) {
                    this.revealGraph(!this.reducedMotion);
                }
                break;
            case 'title':
            case 'gameover':
                this.legFrame = Math.floor(this.clock * 2) % 2;
                break;
            case 'ready':
                this.updatePlayer(dt);
                if (this.stateTime >= 1.4) {
                    this.setState('playing');
                }
                break;
            case 'playing':
                this.updatePlaying(dt);
                break;
            case 'dying':
                if (this.stateTime >= 1.6) {
                    if (this.lives > 0) {
                        this.player.alive = true;
                        this.player.x = this.width / 2;
                        this.bombs = [];
                        this.setState('playing');
                    } else {
                        this.gameOver();
                    }
                }
                break;
            case 'cleared':
                if (this.stateTime >= 2.2) {
                    this.wave++;
                    this.buildWave();
                    this.setState('ready');
                }
                break;
        }

        if (this.state !== 'paused') {
            this.updateEffects(dt);
        }
    }

    updatePlayer(dt) {
        const player = this.player;
        if (!player.alive) {
            return;
        }
        const half = player.w / 2;
        if (this.keys.left !== this.keys.right) {
            player.x += (this.keys.left ? -1 : 1) * 120 * dt;
        } else if (this.pointerX !== null) {
            const delta = this.pointerX - player.x;
            player.x += clamp(delta, -240 * dt, 240 * dt);
        }
        player.x = clamp(player.x, half + 2, this.width - half - 2);
    }

    updatePlaying(dt) {
        this.updatePlayer(dt);
        if (this.keys.fire || this.pointerFire) {
            this.fire();
        }

        this.updateBullet(dt);
        if (this.state !== 'playing') {
            return;
        }
        this.updateFormation(dt);
        if (this.state !== 'playing') {
            return;
        }
        this.updateBombs(dt);
        this.updateSatellite(dt);

        if (this.alive === 0 && this.state === 'playing') {
            if (this.hardcore) {
                this.outage.restartAll();
            }
            this.addScore(50 * this.wave);
            this.sound.satelliteStop();
            this.satellite = null;
            this.bombs = [];
            this.bullet = null;
            this.sound.waveClear();
            this.setState('cleared');
        }
    }

    updateBullet(dt) {
        const bullet = this.bullet;
        if (!bullet) {
            return;
        }
        bullet.y -= 260 * dt;
        if (bullet.y + bullet.h < HUD_HEIGHT) {
            this.bullet = null;
            return;
        }

        for (const enemy of this.enemies) {
            if (!enemy.alive) {
                continue;
            }
            const p = this.enemyPosition(enemy);
            if (rectsOverlap(bullet, { x: p.x, y: p.y, w: SPRITE, h: SPRITE + 2 })) {
                this.destroyEnemy(enemy, p);
                this.bullet = null;
                return;
            }
        }

        if (this.satellite && rectsOverlap(bullet, { x: this.satellite.x, y: this.satellite.y, w: this.satelliteSprite.width, h: this.satelliteSprite.height })) {
            const points = pick(SATELLITE_POINTS);
            this.addScore(points);
            this.explode(this.satelliteSprite, this.satellite.x, this.satellite.y, 1.4);
            this.addText(`+${points} ${pick(SATELLITE_MESSAGES)}`, this.satellite.x + (this.satelliteSprite.width / 2), this.satellite.y + 10, '#ff5fd2');
            this.sound.satelliteStop();
            this.sound.satelliteHit();
            this.satellite = null;
            this.bullet = null;
            return;
        }

        for (let i = 0; i < this.bombs.length; i++) {
            if (rectsOverlap(bullet, this.bombs[i])) {
                this.sparkAt(bullet.x, bullet.y);
                this.bombs.splice(i, 1);
                this.bullet = null;
                return;
            }
        }

        for (const firewall of this.firewalls) {
            const hit = firewallCollision(firewall, bullet);
            if (hit) {
                damageFirewall(firewall, hit.x, hit.y, 2.5);
                this.bullet = null;
                return;
            }
        }
    }

    destroyEnemy(enemy, position) {
        enemy.alive = false;
        this.alive--;
        this.addScore(enemy.points);
        this.explode(enemy.resource.sprite, position.x, position.y, 1);
        this.flashes.push({ x: position.x, y: position.y, life: 0.12 });
        const name = shorten(enemy.resource.label, 12);
        const x = position.x + (SPRITE / 2);
        if (this.hardcore && !enemy.resource.placeholder) {
            // In hard core the text describes the real resource, and many resources have no stop command to run. Only the
            // command's outcome can tell, so the hit claims nothing about the resource and STOPPED waits for that outcome.
            this.outage.stop(enemy.resource.id, () => this.addText(`${name}: STOPPED`, x, position.y, enemy.resource.color));
        }
        this.addText(`${name}: ${this.hardcore ? 'HIT' : pick(DESTROYED_STATES)}`, x, position.y, enemy.resource.color);
        this.sound.enemyExplosion();
    }

    updateFormation(dt) {
        this.stepTimer -= dt;
        if (this.stepTimer > 0) {
            return;
        }
        this.stepTimer += marchInterval(this.alive, this.enemies.length, this.wave);

        let minX = Infinity;
        let maxX = -Infinity;
        for (const enemy of this.enemies) {
            if (enemy.alive) {
                const p = this.enemyPosition(enemy);
                minX = Math.min(minX, p.x);
                maxX = Math.max(maxX, p.x + SPRITE);
            }
        }
        if (minX === Infinity) {
            return;
        }

        const step = this.alive <= 3 ? 3 : 2;
        if ((this.direction > 0 && maxX + step > this.width - 4) || (this.direction < 0 && minX - step < 4)) {
            this.offsetY += 6;
            this.direction = -this.direction;
        } else {
            this.offsetX += this.direction * step;
        }
        this.legFrame ^= 1;
        this.sound.marchBeat();

        // The formation erases firewalls it moves through and ends the game if it reaches the ship's row.
        for (const enemy of this.enemies) {
            if (!enemy.alive) {
                continue;
            }
            const p = this.enemyPosition(enemy);
            const rect = { x: p.x, y: p.y, w: SPRITE, h: SPRITE + 2 };
            for (const firewall of this.firewalls) {
                if (rectsOverlap(rect, firewall)) {
                    eraseFirewall(firewall, rect);
                }
            }
            if (rect.y + rect.h >= this.player.y) {
                this.lives = 0;
                this.killPlayer('YOUR CLUSTER WAS OVERRUN');
                return;
            }
        }
    }

    updateBombs(dt) {
        this.bombTimer -= dt;
        const maxBombs = Math.min(8, 2 + this.wave);
        if (this.bombTimer <= 0 && this.bombs.length < maxBombs) {
            this.bombTimer = Math.max(0.25, 1.3 * Math.pow(0.85, this.wave - 1)) * (0.5 + Math.random());
            this.dropBomb();
        }

        const speed = Math.min(130, 60 + (this.wave * 6));
        const groundY = this.height - 12;
        for (let i = this.bombs.length - 1; i >= 0; i--) {
            const bomb = this.bombs[i];
            bomb.y += speed * dt;
            bomb.frame += dt * 10;
            let remove = false;
            if (bomb.y + bomb.h >= groundY) {
                remove = true;
                this.sparkAt(bomb.x + 1, groundY - 1);
            } else if (this.player.alive && rectsOverlap(bomb, { x: this.player.x - (this.player.w / 2) + 2, y: this.player.y + 3, w: this.player.w - 4, h: this.player.h - 3 })) {
                this.bombs.splice(i, 1);
                this.killPlayer(null);
                return;
            } else {
                for (const firewall of this.firewalls) {
                    const hit = firewallCollision(firewall, bomb);
                    if (hit) {
                        damageFirewall(firewall, hit.x, hit.y + 1, 2.5);
                        remove = true;
                        break;
                    }
                }
            }
            if (remove) {
                this.bombs.splice(i, 1);
            }
        }
    }

    dropBomb() {
        // The bottom enemy of a random column spawns the bomb; a third of the time it's the column above the ship.
        const bottoms = new Map();
        for (const enemy of this.enemies) {
            if (enemy.alive) {
                const current = bottoms.get(enemy.col);
                if (!current || enemy.row > current.row) {
                    bottoms.set(enemy.col, enemy);
                }
            }
        }
        const candidates = [...bottoms.values()];
        if (candidates.length === 0) {
            return;
        }
        let shooter = pick(candidates);
        if (Math.random() < 0.33) {
            let best = Infinity;
            for (const enemy of candidates) {
                const distance = Math.abs(this.enemyPosition(enemy).x + (SPRITE / 2) - this.player.x);
                if (distance < best) {
                    best = distance;
                    shooter = enemy;
                }
            }
        }
        const p = this.enemyPosition(shooter);
        this.bombs.push({ x: p.x + (SPRITE / 2) - 1, y: p.y + SPRITE + 2, w: 3, h: 7, frame: 0 });
    }

    updateSatellite(dt) {
        if (this.satellite) {
            this.satellite.x += this.satellite.direction * (40 + (this.wave * 3)) * dt;
            if (this.satellite.x < -this.satelliteSprite.width - 2 || this.satellite.x > this.width + 2) {
                this.satellite = null;
                this.sound.satelliteStop();
            }
            return;
        }
        this.satelliteTimer -= dt;
        if (this.satelliteTimer <= 0 && this.alive >= 4) {
            this.satelliteTimer = 18 + (Math.random() * 12);
            const direction = Math.random() < 0.5 ? 1 : -1;
            this.satellite = { x: direction > 0 ? -this.satelliteSprite.width : this.width, y: HUD_HEIGHT + 2, direction };
            this.sound.satelliteStart();
        }
    }

    killPlayer(message) {
        const player = this.player;
        player.alive = false;
        this.lives = Math.max(0, this.lives - 1);
        this.explode(this.ship, player.x - (player.w / 2), player.y, 2);
        this.addText(message ?? (this.lives > 0 ? 'ASPIRE RESTARTING...' : 'ASPIRE STOPPED'), player.x, player.y - 8, '#ffffff');
        this.shake = this.reducedMotion ? 0 : 0.5;
        this.bullet = null;
        this.sound.satelliteStop();
        this.satellite = null;
        this.sound.playerHit();
        // The explosion plays before the ship comes back or the game over screen appears.
        this.setState('dying');
    }

    gameOver() {
        this.newBest = false;
        this.recordBestScore();
        this.gameOverMessage = pick(GAME_OVER_MESSAGES);
        this.sound.gameOver();
        this.setState('gameover');
    }

    // --- Effects -----------------------------------------------------------------------------------------------------

    explode(sprite, x, y, force) {
        // Each pixel of the sprite becomes a particle, so resources disintegrate in their own colors.
        const cx = x + (sprite.width / 2);
        const cy = y + (sprite.height / 2);
        for (const pixel of sprite.pixels) {
            if (this.particles.length >= 800) {
                break;
            }
            const px = x + pixel.x;
            const py = y + pixel.y;
            const angle = Math.atan2(py - cy, px - cx) + ((Math.random() - 0.5) * 0.8);
            const speed = (20 + (Math.random() * 50)) * force;
            const life = 0.5 + (Math.random() * 0.6);
            this.particles.push({ x: px, y: py, vx: Math.cos(angle) * speed, vy: (Math.sin(angle) * speed) - 15, life, max: life, color: pixel.color });
        }
    }

    sparkAt(x, y) {
        for (let i = 0; i < 6; i++) {
            const angle = Math.random() * TAU;
            const life = 0.2 + (Math.random() * 0.2);
            this.particles.push({ x, y, vx: Math.cos(angle) * 40, vy: Math.sin(angle) * 40, life, max: life, color: '#ffffff' });
        }
    }

    addText(text, x, y, color) {
        this.texts.push({ text, x, y, color, life: 1.3 });
    }

    updateEffects(dt) {
        for (let i = this.particles.length - 1; i >= 0; i--) {
            const particle = this.particles[i];
            particle.life -= dt;
            if (particle.life <= 0) {
                this.particles.splice(i, 1);
                continue;
            }
            particle.vy += 70 * dt;
            particle.x += particle.vx * dt;
            particle.y += particle.vy * dt;
        }
        for (let i = this.texts.length - 1; i >= 0; i--) {
            const text = this.texts[i];
            text.life -= dt;
            text.y -= 12 * dt;
            if (text.life <= 0) {
                this.texts.splice(i, 1);
            }
        }
        for (let i = this.flashes.length - 1; i >= 0; i--) {
            this.flashes[i].life -= dt;
            if (this.flashes[i].life <= 0) {
                this.flashes.splice(i, 1);
            }
        }
    }

    // --- Rendering ---------------------------------------------------------------------------------------------------

    backgroundAlpha() {
        const ms = this.stateTime * 1000;
        if (this.state === 'morph-in') {
            return clamp(ms / (this.reducedMotion ? REDUCED_MORPH : 600), 0, 1) * BACKGROUND_ALPHA;
        }
        if (this.state === 'morph-out') {
            const duration = this.reducedMotion ? REDUCED_MORPH : MORPH_OUT;
            return (1 - clamp(ms / (duration * 0.85), 0, 1)) * BACKGROUND_ALPHA;
        }
        return BACKGROUND_ALPHA;
    }

    render() {
        const ctx = this.ctx;
        const fade = this.backgroundAlpha() / BACKGROUND_ALPHA;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, this.width, this.height);
        ctx.globalAlpha = 1;
        ctx.fillStyle = `rgba(4, 2, 14, ${this.backgroundAlpha()})`;
        ctx.fillRect(0, 0, this.width, this.height);

        const flicker = this.reducedMotion ? 1 : 0.95 + (Math.random() * 0.05);
        this.crt.style.opacity = String(fade * flicker);
        this.buttons.style.opacity = String(fade);

        this.renderStars(fade);

        if (this.state === 'morph-in') {
            this.renderMorphIn();
            return;
        }
        if (this.state === 'morph-out') {
            this.renderMorphOut();
            return;
        }

        if (this.shake > 0) {
            ctx.translate(Math.round((Math.random() - 0.5) * 4), Math.round((Math.random() - 0.5) * 4));
        }
        this.renderWorld();
        this.renderOverlay();
    }

    renderStars(fade) {
        const ctx = this.ctx;
        for (const star of this.stars) {
            const twinkle = 0.3 + (0.3 * Math.sin((this.clock * 2) + star.phase));
            ctx.fillStyle = `rgba(200, 190, 255, ${twinkle * fade})`;
            ctx.fillRect(Math.floor(star.x), Math.floor(star.y), 1, 1);
        }
    }

    renderEnemy(enemy, x, y, alpha = 1) {
        const ctx = this.ctx;
        const sprite = enemy.resource.sprite;
        ctx.globalAlpha = alpha;
        ctx.drawImage(sprite.canvas, x, y);
        ctx.fillStyle = enemy.resource.color;
        for (const [lx, ly] of LEGS[this.legFrame]) {
            ctx.fillRect(x + lx, y + SPRITE + ly, 1, 1);
        }
        ctx.globalAlpha = 1;
    }

    renderWorld() {
        const ctx = this.ctx;

        // HUD.
        drawText(ctx, `SCORE ${padScore(this.score)}`, 4, 4, '#ffffff');
        drawText(ctx, `HI ${padScore(Math.max(this.best, this.score))}`, this.width / 2, 4, '#b9aaee', 1, 'center');
        drawText(ctx, `WAVE ${this.wave}`, this.width - 4, 4, '#ffffff', 1, 'right');

        for (const firewall of this.firewalls) {
            this.renderFirewall(firewall);
        }

        // During "ready" the formation slides into place.
        let slideY = 0;
        let enemyAlpha = 1;
        if (this.state === 'ready' && !this.reducedMotion) {
            const p = easeInOutCubic(clamp(this.stateTime / 0.9, 0, 1));
            slideY = Math.round(-(1 - p) * 60);
            enemyAlpha = p;
        }
        for (const enemy of this.enemies) {
            if (enemy.alive) {
                const p = this.enemyPosition(enemy);
                this.renderEnemy(enemy, p.x, p.y + slideY, enemyAlpha);
            }
        }

        for (const flash of this.flashes) {
            ctx.drawImage(this.sparkSprite.canvas, flash.x, flash.y);
        }

        if (this.satellite) {
            ctx.drawImage(this.satelliteSprite.canvas, Math.round(this.satellite.x), this.satellite.y);
        }

        const player = this.player;
        if (player.alive && (this.state !== 'ready' || Math.floor(this.stateTime * 8) % 2 === 0)) {
            ctx.drawImage(this.ship.canvas, Math.round(player.x - (player.w / 2)), player.y);
        }

        if (this.bullet) {
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(this.bullet.x, Math.round(this.bullet.y), this.bullet.w, this.bullet.h);
        }

        ctx.fillStyle = '#ffd36b';
        for (const bomb of this.bombs) {
            for (const [bx, by] of BOMB_FRAMES[Math.floor(bomb.frame) % 2]) {
                ctx.fillRect(Math.round(bomb.x) + bx, Math.round(bomb.y) + by, 1, 1);
            }
        }

        for (const particle of this.particles) {
            ctx.globalAlpha = clamp(particle.life / particle.max, 0, 1);
            ctx.fillStyle = particle.color;
            ctx.fillRect(Math.round(particle.x), Math.round(particle.y), 1, 1);
        }
        ctx.globalAlpha = 1;

        for (const text of this.texts) {
            const half = textWidth(text.text) / 2;
            const x = clamp(text.x, half + 2, this.width - half - 2);
            ctx.globalAlpha = clamp(text.life / 0.4, 0, 1);
            drawText(ctx, text.text, x, Math.round(text.y), text.color, 1, 'center');
        }
        ctx.globalAlpha = 1;

        // Ground and remaining lives.
        const groundY = this.height - 12;
        ctx.fillStyle = '#7455dd';
        ctx.fillRect(0, groundY, this.width, 1);
        for (let i = 0; i < this.lives; i++) {
            ctx.drawImage(this.lifeIcon.canvas, 4 + (i * 10), groundY + 3);
        }
    }

    renderFirewall(firewall) {
        if (firewall.dirty || !firewall.canvas) {
            firewall.canvas ??= createCanvas(firewall.w, firewall.h);
            const ctx = firewall.canvas.getContext('2d');
            ctx.clearRect(0, 0, firewall.w, firewall.h);
            ctx.fillStyle = '#b9aaee';
            for (let row = 0; row < firewall.h; row++) {
                for (let col = 0; col < firewall.w; col++) {
                    if (firewall.cells[(row * firewall.w) + col]) {
                        ctx.fillRect(col, row, 1, 1);
                    }
                }
            }
            firewall.dirty = false;
        }
        this.ctx.drawImage(firewall.canvas, firewall.x, firewall.y);
    }

    renderPanel(top, height) {
        const ctx = this.ctx;
        const width = Math.min(this.width - 8, 300);
        const left = Math.round((this.width - width) / 2);
        ctx.fillStyle = 'rgba(4, 2, 14, 0.85)';
        ctx.fillRect(left, top, width, height);
        ctx.fillStyle = '#512bd4';
        ctx.fillRect(left, top, width, 1);
        ctx.fillRect(left, top + height - 1, width, 1);
        ctx.fillRect(left, top, 1, height);
        ctx.fillRect(left + width - 1, top, 1, height);
    }

    // The mode controls are DOM, sized in CSS pixels, so how much of the playfield they take depends on its scale.
    modesTop() {
        return this.screenToWorld(0, this.modes.getBoundingClientRect().top).y;
    }

    panelTop(height) {
        return Math.max(HUD_HEIGHT + 2, Math.min(Math.round((this.height - height) / 2), Math.round(this.modesTop() - height - 2)));
    }

    renderOverlay() {
        const ctx = this.ctx;
        const cx = this.width / 2;
        const blink = Math.floor(this.clock * 2.5) % 2 === 0;

        switch (this.state) {
            case 'title': {
                // On a short graph the panel lists fewer resources, to end above the mode controls.
                const rows = clamp(Math.floor((this.modesTop() - HUD_HEIGHT - 2 - 136) / 14), 0, 5);
                const shown = this.roster.slice(0, rows);
                const height = 126 + (shown.length * 14) + (this.roster.length > shown.length ? 10 : 0);
                const top = this.panelTop(height);
                this.renderPanel(top, height);
                drawText(ctx, 'ASPIRE', cx, top + 8, '#dcd5f6', 2, 'center');
                drawText(ctx, 'OUTAGE', cx, top + 26, '#9780e5', 2, 'center');
                drawText(ctx, `HI-SCORE ${padScore(this.best)}`, cx, top + 46, '#ffffff', 1, 'center');

                let y = top + 60;
                const left = Math.round(cx - 70);
                for (const entry of shown) {
                    ctx.drawImage(entry.sprite.canvas, left, y - 2);
                    drawText(ctx, shorten(entry.label, 20), left + 18, y, entry.color);
                    y += 14;
                }
                if (this.roster.length > shown.length) {
                    drawText(ctx, `AND ${this.roster.length - shown.length} MORE...`, left + 18, y, '#b9aaee');
                    y += 10;
                }
                ctx.drawImage(this.satelliteSprite.canvas, left - 2, y);
                drawText(ctx, 'TELEMETRY SATELLITE', left + 18, y, '#ff5fd2');
                y += 16;
                drawText(ctx, this.hardcore ? 'MODE: HARD CORE' : 'MODE: NORMAL', cx, y, this.hardcore ? '#ff5f5f' : '#ffffff', 1, 'center');
                y += 12;
                if (blink) {
                    drawText(ctx, 'PRESS SPACE OR CLICK TO START', cx, y, '#7dffa0', 1, 'center');
                }
                // The hard core warning needs this room. The keys remain in the game's accessible description.
                if (!this.hardcore) {
                    drawText(ctx, 'ARROWS/A-D OR MOUSE MOVE', cx, y + 14, '#8a80b0', 1, 'center');
                    drawText(ctx, 'SPACE/CLICK FIRE  P PAUSE  M MUTE  H MODE', cx, y + 24, '#8a80b0', 1, 'center');
                }
                break;
            }
            case 'ready':
                drawText(ctx, `WAVE ${this.wave}`, cx, Math.round(this.height * 0.55), '#ffffff', 2, 'center');
                drawText(ctx, this.wave === 1 ? 'ALL HEALTH CHECKS FAILING' : 'SCALING OUT...', cx, Math.round(this.height * 0.55) + 20, '#b9aaee', 1, 'center');
                break;
            case 'paused':
                this.renderPanel(Math.round(this.height / 2) - 20, 40);
                drawText(ctx, 'PAUSED', cx, Math.round(this.height / 2) - 12, '#ffffff', 2, 'center');
                // P only reaches the game while it has focus. Otherwise it's the dashboard's shortcut to the parameters page.
                drawText(ctx, this.root.contains(document.activeElement) ? 'P OR CLICK TO RESUME' : 'CLICK TO RESUME', cx, Math.round(this.height / 2) + 6, '#b9aaee', 1, 'center');
                break;
            case 'cleared':
                drawText(ctx, `WAVE ${this.wave} CLEARED`, cx, Math.round(this.height * 0.45), '#7dffa0', 2, 'center');
                drawText(ctx, this.hardcore ? 'RESTARTING RESOURCES...' : 'ALL RESOURCES STOPPED', cx, Math.round(this.height * 0.45) + 20, '#ffffff', 1, 'center');
                drawText(ctx, `BONUS +${50 * this.wave}`, cx, Math.round(this.height * 0.45) + 32, '#ffd36b', 1, 'center');
                break;
            case 'gameover': {
                const top = this.panelTop(100);
                this.renderPanel(top, 100);
                drawText(ctx, 'GAME OVER', cx, top + 8, '#ff5f5f', 2, 'center');
                drawText(ctx, this.gameOverMessage, cx, top + 28, '#b9aaee', 1, 'center');
                drawText(ctx, `SCORE    ${padScore(this.score)}`, cx, top + 42, '#ffffff', 1, 'center');
                drawText(ctx, `HI-SCORE ${padScore(this.best)}`, cx, top + 52, '#ffffff', 1, 'center');
                if (this.newBest && blink) {
                    drawText(ctx, 'NEW HIGH SCORE!', cx, top + 64, '#ffd36b', 1, 'center');
                }
                if (this.stateTime > 1) {
                    drawText(ctx, 'SPACE OR CLICK TO PLAY AGAIN', cx, top + 78, '#7dffa0', 1, 'center');
                    drawText(ctx, 'ESC TO RETURN TO THE GRAPH', cx, top + 88, '#8a80b0', 1, 'center');
                }
                break;
            }
        }
    }

    renderNode(entry, x, y, radius, spriteScale, ringAlpha, alpha = 1) {
        const ctx = this.ctx;
        if (ringAlpha > 0 && radius > 0) {
            ctx.globalAlpha = ringAlpha * alpha;
            ctx.beginPath();
            ctx.arc(x, y, radius, 0, TAU);
            ctx.fillStyle = entry.fill;
            ctx.fill();
            ctx.lineWidth = Math.max(1, radius * 0.08);
            ctx.strokeStyle = entry.color;
            ctx.stroke();
        }
        const sprite = entry.sprite;
        const w = sprite.width * spriteScale;
        const h = sprite.height * spriteScale;
        ctx.globalAlpha = alpha;
        ctx.drawImage(sprite.canvas, Math.round(x - (w / 2)), Math.round(y - (h / 2)), Math.round(w), Math.round(h));
        ctx.globalAlpha = 1;
    }

    renderLinks(alpha, positions) {
        if (alpha <= 0) {
            return;
        }
        const ctx = this.ctx;
        ctx.globalAlpha = alpha;
        ctx.strokeStyle = '#9780e5';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (const [a, b] of this.links) {
            ctx.moveTo(positions[a].x, positions[a].y);
            ctx.lineTo(positions[b].x, positions[b].y);
        }
        ctx.stroke();
        ctx.globalAlpha = 1;
    }

    // A node's icon fills about 43% of its circle, so this is the sprite scale that matches the node's on-screen icon.
    nodeSpriteScale(radius) {
        return Math.max(1, (radius * 0.86) / SPRITE);
    }

    renderMorphIn() {
        const ms = this.stateTime * 1000;
        if (this.reducedMotion) {
            const p = clamp(ms / REDUCED_MORPH, 0, 1);
            for (const entry of this.roster) {
                this.renderNode(entry, entry.from.x, entry.from.y, entry.from.r, this.nodeSpriteScale(entry.from.r), 1, 1 - p);
            }
            for (const enemy of this.enemies) {
                const position = this.enemyPosition(enemy);
                this.renderEnemy(enemy, position.x, position.y, p);
            }
            return;
        }

        this.renderLinks(1 - clamp(ms / MORPH_PIXELATE, 0, 1), this.roster.map(entry => entry.from));

        // The first enemy of each resource is the node itself; the copies split off it one after another.
        const started = new Set();
        this.enemies.forEach((enemy, index) => {
            const entry = enemy.resource;
            const isOriginal = !started.has(entry);
            started.add(entry);
            const delay = MORPH_PIXELATE + (Math.min(index, 30) * MORPH_STAGGER);
            const p = easeInOutCubic(clamp((ms - delay) / MORPH_FLIGHT, 0, 1));
            if (p <= 0 && !isOriginal) {
                return;
            }

            const slot = this.enemyPosition(enemy);
            const to = { x: slot.x + (SPRITE / 2), y: slot.y + (SPRITE / 2) };
            const from = entry.from;
            // Fly along a curve that bulges upward and alternates sideways, so enemies don't move in straight lines.
            const control = { x: ((from.x + to.x) / 2) + ((index % 2 === 0 ? 1 : -1) * 24), y: Math.min(from.y, to.y) - 40 };
            const q = 1 - p;
            const x = (q * q * from.x) + (2 * q * p * control.x) + (p * p * to.x);
            const y = (q * q * from.y) + (2 * q * p * control.y) + (p * p * to.y);
            const radius = lerp(from.r, SPRITE * 0.6, p);
            const scale = lerp(this.nodeSpriteScale(from.r), 1, p);
            this.renderNode(entry, x, y, radius, scale, isOriginal ? 1 - p : 0, isOriginal ? 1 : clamp(p * 3, 0, 1));
            if (p >= 1) {
                this.renderLegs(enemy, slot.x, slot.y);
            }
        });
    }

    renderLegs(enemy, x, y) {
        const ctx = this.ctx;
        ctx.fillStyle = enemy.resource.color;
        for (const [lx, ly] of LEGS[this.legFrame]) {
            ctx.fillRect(x + lx, y + SPRITE + ly, 1, 1);
        }
    }

    renderMorphOut() {
        const ms = this.stateTime * 1000;
        if (this.reducedMotion) {
            const p = clamp(ms / REDUCED_MORPH, 0, 1);
            for (const flight of this.outbound) {
                this.renderNode(flight.entry, flight.from.x, flight.from.y, 0, 1, 0, 1 - p);
            }
            return;
        }

        const p = easeInOutCubic(clamp(ms / MORPH_OUT, 0, 1));
        // Copies fade out where they are while one enemy per resource flies home.
        if (this.wasInFormation) {
            const copiesAlpha = 1 - clamp(ms / 300, 0, 1);
            if (copiesAlpha > 0) {
                const flying = new Set(this.outbound.map(flight => flight.enemy));
                for (const enemy of this.enemies) {
                    if (enemy.alive && !flying.has(enemy)) {
                        const position = this.enemyPosition(enemy);
                        this.renderEnemy(enemy, position.x, position.y, copiesAlpha);
                    }
                }
            }
        }

        const positions = [];
        this.outbound.forEach((flight, index) => {
            const { from, to, entry } = flight;
            const control = { x: ((from.x + to.x) / 2) + ((index % 2 === 0 ? -1 : 1) * 24), y: Math.min(from.y, to.y) - 40 };
            const q = 1 - p;
            const x = (q * q * from.x) + (2 * q * p * control.x) + (p * p * to.x);
            const y = (q * q * from.y) + (2 * q * p * control.y) + (p * p * to.y);
            positions[this.roster.indexOf(entry)] = { x, y };
            const radius = lerp(SPRITE * 0.6, to.r, p);
            const scale = lerp(1, this.nodeSpriteScale(to.r), p);
            // Fade out at the end as the real graph fades back in underneath.
            const alpha = 1 - clamp((ms - (MORPH_OUT * 0.8)) / 300, 0, 1);
            this.renderNode(entry, x, y, radius, scale, p, alpha);
        });
        this.renderLinks(clamp((p - 0.6) / 0.4, 0, 1) * (1 - clamp((ms - (MORPH_OUT * 0.8)) / 300, 0, 1)), positions);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Launch
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Wires up the graph's Play button and the O key on the focused graph. These listeners are all that exists until the game
 * is launched.
 */
export function attachResourceGraphGame(graph, strings) {
    const svg = typeof graph?.svg?.node === 'function' ? graph.svg.node() : null;
    const container = svg?.closest?.('.resource-graph-container');
    if (!container || attachedGraphs.has(svg)) {
        return;
    }
    attachedGraphs.add(svg);

    // The container outlives this SVG when the graph is rendered again, hence the isConnected checks. stopPropagation
    // keeps the key from reaching the dashboard's global shortcuts, where O opens the telemetry sources.
    const onKeyDown = (event) => {
        if (!activeGame && svg.isConnected && isLaunchKey(event)) {
            event.preventDefault();
            event.stopPropagation();
            launchGame(graph, svg, null, strings);
        }
    };
    const onPlayClick = (event) => {
        if (svg.isConnected) {
            launchGame(graph, svg, event.currentTarget, strings);
        }
    };

    container.addEventListener('keydown', onKeyDown);
    container.querySelector('.graph-game')?.addEventListener('click', onPlayClick);
}

function launchGame(graph, svg, opener, strings) {
    if (activeGame) {
        return;
    }
    const container = svg.closest('.resource-graph-container');
    if (!container || !isShown(container)) {
        return;
    }
    activeGame = new ResourceGraphGame(graph, svg, container, opener, strings, () => {
        activeGame = null;
    });
}

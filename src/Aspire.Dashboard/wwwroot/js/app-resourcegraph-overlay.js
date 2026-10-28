// Interactive overlay for the resource graph.
//
// Drawing two full circles with the mouse over empty graph space (without pressing a button, so it can't happen while
// panning or dragging a node) opens a canvas overlay in which the graph's resources morph into a grid layout. Escape or
// the close button morphs everything back into the graph.
//
// The overlay is self-contained plain JavaScript drawn on a canvas. It never changes the graph's data or d3 state:
// while open it only sets inline opacity/visibility on the graph SVG and its controls, and restores the previous
// style attributes on exit. Until the gesture is recognized there is no animation frame, no keyboard listener and no
// audio context, only a cheap pointermove listener on the graph SVG.

const TAU = Math.PI * 2;

// Overlay text, kept together as base64 encoded JSON. {0} is replaced by format().
const LABELS = JSON.parse(atob('eyJodWRMZWZ0IjoiU0NPUkUiLCJodWRDZW50ZXIiOiJISSIsImh1ZFJpZ2h0IjoiV0FWRSIsInRpdGxlVG9wIjoiQVNQSVJFIiwidGl0bGVCb3R0b20iOiJJTlZBREVSUyIsInJlY29yZExpbmUiOiJISS1TQ09SRSIsIm1vcmUiOiJBTkQgezB9IE1PUkUuLi4iLCJiZWFjb24iOiI9ID8/PyBPVExQIE1PVEhFUlNISVAiLCJzdGFydCI6IlBSRVNTIFNQQUNFIE9SIENMSUNLIFRPIFNUQVJUIiwiaGVscDEiOiJBUlJPV1MvQS1EIE9SIE1PVVNFIE1PVkUiLCJoZWxwMiI6IlNQQUNFL0NMSUNLIEZJUkUgIFAgUEFVU0UgIE0gTVVURSAgRVNDIEVYSVQiLCJzdGFnZUZpcnN0IjoiQUxMIEhFQUxUSCBDSEVDS1MgRkFJTElORyIsInN0YWdlTmV4dCI6IlNDQUxJTkcgT1VULi4uIiwicGF1c2VkIjoiUEFVU0VEIiwicmVzdW1lIjoiUCBUTyBSRVNVTUUiLCJjbGVhcmVkIjoiV0FWRSB7MH0gQ0xFQVJFRCIsImNsZWFyZWROb3RlIjoiQUxMIFJFU09VUkNFUyBTVE9QUEVEIiwiYm9udXMiOiJCT05VUyArezB9IiwiZmluaXNoZWQiOiJHQU1FIE9WRVIiLCJ0YWxseUxpbmUiOiJTQ09SRSAgICIsIm5ld1JlY29yZCI6Ik5FVyBISUdIIFNDT1JFISIsImFnYWluIjoiU1BBQ0UgT1IgQ0xJQ0sgVE8gUExBWSBBR0FJTiIsImJhY2siOiJFU0MgVE8gUkVUVVJOIFRPIFRIRSBHUkFQSCIsInVuaXQiOiIrMSBSRVBMSUNBIiwicmVzdGFydCI6IkFTUElSRSBSRVNUQVJUSU5HLi4uIiwic3RvcHBlZCI6IkFTUElSRSBTVE9QUEVEIiwib3ZlcnJ1biI6IllPVVIgQ0xVU1RFUiBXQVMgT1ZFUlJVTiIsInJvb3RMYWJlbCI6IkFzcGlyZSBJbnZhZGVycy4gQXJyb3cga2V5cyBvciBBIGFuZCBEIHRvIG1vdmUsIFNwYWNlIHRvIGZpcmUsIFAgdG8gcGF1c2UsIE0gdG8gbXV0ZSwgRXNjYXBlIHRvIHJldHVybiB0byB0aGUgZ3JhcGguIiwiZXhpdExhYmVsIjoiRXhpdCBBc3BpcmUgSW52YWRlcnMgYW5kIHJldHVybiB0byB0aGUgZ3JhcGgifQ=='));

function format(label, value) {
    return label.replace('{0}', String(value));
}

const RECORD_KEY = 'aspire-resource-graph-overlay-1';
const MUTED_KEY = 'aspire-resource-graph-overlay-2';

export const MAX_ITEMS = 66;
const MAX_COLUMNS = 11;
const BONUS_UNIT_TALLY = 2500;
const MAX_UNITS = 6;

// World units are "big pixels". The canvas is rendered at this low resolution and scaled up by CSS with
// image-rendering: pixelated, which keeps every sprite, line and letter crisp.
const SPRITE = 12;
const SPACING_X = 18;
const SPACING_Y = 18;
const HUD_HEIGHT = 14;
const BACKGROUND_ALPHA = 0.94;

// Morph timings in milliseconds. Reduced motion replaces the flight with a short cross-fade.
const MORPH_PIXELATE = 350;
const MORPH_FLIGHT = 800;
const MORPH_STAGGER = 15;
const MORPH_OUT = 900;
const REDUCED_MORPH = 220;

const EXIT_STATES = ['EXITED', 'FAILEDTOSTART', 'FINISHED', 'UNHEALTHY', 'STOPPED', 'EXIT CODE 137', 'OOMKILLED', 'RUNTIMEUNHEALTHY', 'CRASHLOOP', 'NOT FOUND'];
const BEACON_LABELS = ['SPANS INGESTED', 'TRACES COLLECTED', 'LOGS EXPORTED', 'METRICS SCRAPED'];
const FINISH_LABELS = ['INCIDENT REPORT FILED', 'ON-CALL HAS BEEN PAGED', 'HAVE YOU TRIED ASPIRE RUN?', 'ALL HEALTH CHECKS FAILING', 'WORKS ON MY MACHINE'];
const BEACON_VALUES = [50, 100, 150, 300];

// ---------------------------------------------------------------------------------------------------------------------
// Pure logic
// ---------------------------------------------------------------------------------------------------------------------

export const DEFAULT_GESTURE = Object.freeze({
    turns: 2,
    minRadius: 25,
    maxRadius: 500,
    maxVariation: 0.4,
    minPoints: 20,
    windowMs: 3500,
});

export function normalizeAngle(angle) {
    while (angle > Math.PI) {
        angle -= TAU;
    }
    while (angle < -Math.PI) {
        angle += TAU;
    }
    return angle;
}

/**
 * Measures how circular a pointer trail is around its own centroid: the signed number of turns swept, the mean radius
 * and the radius' coefficient of variation (0 for a perfect circle).
 */
export function measureCircularity(points, start = 0) {
    const count = points.length - start;
    let cx = 0;
    let cy = 0;
    for (let i = start; i < points.length; i++) {
        cx += points[i].x;
        cy += points[i].y;
    }
    cx /= count;
    cy /= count;

    let radiusSum = 0;
    let radiusSquareSum = 0;
    let swept = 0;
    let previousAngle = 0;
    for (let i = start; i < points.length; i++) {
        const dx = points[i].x - cx;
        const dy = points[i].y - cy;
        const radius = Math.hypot(dx, dy);
        radiusSum += radius;
        radiusSquareSum += radius * radius;
        const angle = Math.atan2(dy, dx);
        if (i > start) {
            swept += normalizeAngle(angle - previousAngle);
        }
        previousAngle = angle;
    }

    const radius = radiusSum / count;
    const variance = Math.max(0, (radiusSquareSum / count) - (radius * radius));
    return {
        turns: swept / TAU,
        radius,
        variation: radius > 0 ? Math.sqrt(variance) / radius : Infinity,
    };
}

/**
 * Returns true when the trail ends with at least two full circles (or a loose spiral) drawn in one direction.
 * Every suffix of the trail is tried because the circles are usually preceded by unrelated movement that would move
 * the centroid off the circles' center.
 */
export function detectCircleGesture(points, options) {
    const o = { ...DEFAULT_GESTURE, ...options };
    if (points.length < o.minPoints) {
        return false;
    }

    const requiredTurns = o.turns * 0.92;
    const step = Math.max(1, Math.floor(points.length / 24));
    for (let start = 0; start <= points.length - o.minPoints; start += step) {
        const m = measureCircularity(points, start);
        if (Math.abs(m.turns) >= requiredTurns &&
            m.radius >= o.minRadius &&
            m.radius <= o.maxRadius &&
            m.variation <= o.maxVariation) {
            return true;
        }
    }

    return false;
}

/**
 * Lays out the item grid. Every resource in the graph appears at least once. Tiny graphs are repeated to fill a
 * proper grid, and later stages get bigger, up to MAX_ITEMS.
 */
export function buildGrid(resourceCount, stage) {
    const count = Math.max(1, resourceCount);
    const minimum = Math.min(MAX_ITEMS, 15 + ((stage - 1) * 6));
    const total = Math.min(MAX_ITEMS, Math.max(count, minimum));
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

/** The top third of the grid weighs 30, the middle 20 and the bottom 10. */
export function itemWeight(row, rows) {
    const tier = rows <= 1 ? 0 : row / (rows - 1);
    if (tier < 0.34) {
        return 30;
    }
    return tier < 0.67 ? 20 : 10;
}

/** Seconds between two grid steps. Steps get faster as items are removed and with every stage. */
export function stepInterval(alive, total, stage) {
    const base = Math.max(0.28, 0.8 * Math.pow(0.86, stage - 1));
    const fraction = total > 0 ? alive / total : 0;
    return Math.max(0.03, base * (0.04 + (0.96 * fraction)));
}

/** Number of extra units earned by going from previousTally to tally. */
export function bonusUnits(previousTally, tally) {
    return Math.floor(tally / BONUS_UNIT_TALLY) - Math.floor(previousTally / BONUS_UNIT_TALLY);
}

export function rectsOverlap(a, b) {
    return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

// Block silhouette, 22x16 cells.
const BLOCK_ROWS = [
    '    ##############    ',
    '   ################   ',
    '  ##################  ',
    ' #################### ',
    '######################',
    '######################',
    '######################',
    '######################',
    '######################',
    '######################',
    '######################',
    '######################',
    '#######        #######',
    '######          ######',
    '#####            #####',
    '#####            #####',
];

export function createBlock(x, y) {
    const h = BLOCK_ROWS.length;
    const w = BLOCK_ROWS[0].length;
    const cells = new Uint8Array(w * h);
    for (let row = 0; row < h; row++) {
        for (let col = 0; col < w; col++) {
            cells[(row * w) + col] = BLOCK_ROWS[row][col] === '#' ? 1 : 0;
        }
    }
    return { x, y, w, h, cells, dirty: true, canvas: null };
}

/** Returns the world position of the first solid block cell inside rect, or null. */
export function blockCollision(block, rect) {
    const left = Math.max(0, Math.floor(rect.x - block.x));
    const right = Math.min(block.w - 1, Math.ceil(rect.x + rect.w - block.x) - 1);
    const top = Math.max(0, Math.floor(rect.y - block.y));
    const bottom = Math.min(block.h - 1, Math.ceil(rect.y + rect.h - block.y) - 1);
    for (let row = top; row <= bottom; row++) {
        for (let col = left; col <= right; col++) {
            if (block.cells[(row * block.w) + col]) {
                return { x: block.x + col, y: block.y + row };
            }
        }
    }
    return null;
}

/** Erodes a ragged hole around a world position. Returns the number of cells removed. */
export function erodeBlock(block, x, y, radius, random = Math.random) {
    let removed = 0;
    const r = Math.ceil(radius);
    for (let dy = -r; dy <= r; dy++) {
        for (let dx = -r; dx <= r; dx++) {
            const col = Math.round(x - block.x) + dx;
            const row = Math.round(y - block.y) + dy;
            if (col < 0 || row < 0 || col >= block.w || row >= block.h) {
                continue;
            }
            const distance = Math.hypot(dx, dy);
            // The rim of the hole is dithered so it looks chewed rather than stamped.
            if (distance <= radius - 1 || (distance <= radius && random() < 0.5)) {
                const index = (row * block.w) + col;
                if (block.cells[index]) {
                    block.cells[index] = 0;
                    removed++;
                }
            }
        }
    }
    if (removed > 0) {
        block.dirty = true;
    }
    return removed;
}

/** Erases every block cell under rect, used when the grid moves through a block. */
export function clearBlock(block, rect) {
    let removed = 0;
    for (let row = 0; row < block.h; row++) {
        for (let col = 0; col < block.w; col++) {
            const index = (row * block.w) + col;
            if (block.cells[index] && rectsOverlap(rect, { x: block.x + col, y: block.y + row, w: 1, h: 1 })) {
                block.cells[index] = 0;
                removed++;
            }
        }
    }
    if (removed > 0) {
        block.dirty = true;
    }
    return removed;
}

// Storage can be unavailable (private windows, blocked site data) or throw on access, so every access is guarded and
// the overlay works without it.
export function readRecord(getStorage) {
    try {
        const value = Number.parseInt(getStorage()?.getItem(RECORD_KEY) ?? '', 10);
        return Number.isFinite(value) && value > 0 ? value : 0;
    } catch {
        return 0;
    }
}

export function saveRecord(getStorage, tally) {
    const record = readRecord(getStorage);
    if (tally <= record) {
        return record;
    }
    try {
        getStorage()?.setItem(RECORD_KEY, String(tally));
    } catch {
        // The record is a nicety; ignore storage failures.
    }
    return tally;
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

export function padTally(tally) {
    return String(Math.max(0, Math.floor(tally))).padStart(6, '0');
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

function pick(items) {
    return items[Math.floor(Math.random() * items.length)];
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

// Small bitmaps are stored as one integer per row, most significant bit on the left, and expanded to the
// '#'/' ' row strings that bitmapSprite draws. For example bitRows(3, [0x5, 0x2]) is ['# #', ' # '].
function bitRows(width, values) {
    return values.map(value => value.toString(2).padStart(width, '0').replace(/1/g, '#').replace(/0/g, ' '));
}

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

// Fallback item when a resource has no usable icon.
const FALLBACK_ROWS = bitRows(11, [0x104, 0x88, 0x1fc, 0x376, 0x7ff, 0x5fd, 0x505, 0xd8]);

// The beacon represents OTLP telemetry flying over the app.
const BEACON_ROWS = bitRows(16, [0x7e0, 0x1ff8, 0x3ffc, 0x6db6, 0xffff, 0x399c, 0x1008]);

const SPARK_ROWS = bitRows(12, [0x110, 0x8a2, 0x404, 0x208, 0xc06, 0x208, 0x4a4, 0x912]);

// Two-frame legs drawn under each icon so resource icons appear to walk. Offsets are [x, y below the icon].
const LEGS = [
    [[2, 0], [1, 1], [9, 0], [10, 1]],
    [[2, 0], [3, 1], [9, 0], [8, 1]],
];

const DROP_FRAMES = [
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
            sprite = bitmapSprite(FALLBACK_ROWS, color);
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

class AudioCues {
    constructor(muted) {
        this.muted = muted;
        this.context = null;
        this.beaconNodes = null;
        this.pulseIndex = 0;
    }

    // Browsers only allow audio to start from a user activation (click, key press). The recognition gesture is a
    // pointermove, which doesn't count, so the context is created on the first click or key press in the overlay.
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

    ping() {
        this.tone('square', 1400, 220, 0.11, 0.16);
    }

    burst() {
        this.noise(0.28, 0.5, 2400);
        this.tone('square', 300, 60, 0.18, 0.12);
    }

    spark() {
        this.noise(0.08, 0.25, 3000);
    }

    // A four-note descending bass loop. Its tempo is the grid's step rate.
    pulse() {
        const notes = [110, 98, 87.31, 82.41];
        const note = notes[this.pulseIndex++ % notes.length];
        this.tone('square', note, note * 0.94, 0.09, 0.3);
    }

    beaconStart() {
        if (!this.context || this.beaconNodes) {
            return;
        }
        const ctx = this.context;
        const oscillator = ctx.createOscillator();
        oscillator.type = 'square';
        oscillator.frequency.value = 520;
        const lfo = ctx.createOscillator();
        lfo.frequency.value = 6;
        const lfoGain = ctx.createGain();
        lfoGain.gain.value = 180;
        lfo.connect(lfoGain).connect(oscillator.frequency);
        const gain = ctx.createGain();
        gain.gain.value = 0.06;
        oscillator.connect(gain).connect(this.master);
        oscillator.start();
        lfo.start();
        this.beaconNodes = { oscillator, lfo, gain };
    }

    beaconStop() {
        if (!this.beaconNodes) {
            return;
        }
        const now = this.context.currentTime;
        this.beaconNodes.gain.gain.setTargetAtTime(0, now, 0.03);
        this.beaconNodes.oscillator.stop(now + 0.2);
        this.beaconNodes.lfo.stop(now + 0.2);
        this.beaconNodes = null;
    }

    beaconHit() {
        for (let i = 0; i < 4; i++) {
            this.tone('triangle', 1600, 400, 0.1, 0.25, i * 0.08);
        }
    }

    anchorLost() {
        this.noise(1.1, 0.7, 1200);
        this.tone('sawtooth', 440, 40, 1.0, 0.2);
    }

    start() {
        this.arpeggio([131, 196, 262, 392, 523], 0.07, 0.1, 0.18);
    }

    stageClear() {
        this.arpeggio([262, 330, 392, 523, 659, 784], 0.08, 0.12, 0.18);
    }

    extraLife() {
        this.arpeggio([659, 880, 1319], 0.07, 0.1, 0.18, 'triangle');
    }

    finish() {
        this.arpeggio([392, 330, 262, 196, 131], 0.2, 0.22, 0.22, 'square', 0.3);
    }

    powerDown() {
        this.tone('sawtooth', 600, 40, 0.6, 0.15);
    }

    dispose() {
        this.beaconStop();
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
// removed when the overlay closes.
// ---------------------------------------------------------------------------------------------------------------------

const STYLE_ID = 'resource-graph-overlay-style';
const STYLES = `
.resource-graph-overlay { position: absolute; inset: 0; z-index: 20; overflow: hidden; outline: none; cursor: crosshair; user-select: none; touch-action: none; }
.resource-graph-overlay canvas { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; image-rendering: crisp-edges; image-rendering: pixelated; filter: saturate(1.2) drop-shadow(0 0 2px rgba(190, 170, 255, 0.55)); }
.resource-graph-overlay-scan { position: absolute; inset: 0; pointer-events: none; opacity: 0; background: repeating-linear-gradient(to bottom, rgba(0, 0, 0, 0.3) 0, rgba(0, 0, 0, 0.3) 1px, transparent 1px, transparent 3px), radial-gradient(ellipse at center, transparent 55%, rgba(0, 0, 0, 0.6) 100%); }
.resource-graph-overlay-actions { position: absolute; right: 8px; bottom: 6px; display: flex; gap: 6px; opacity: 0; }
.resource-graph-overlay-action { font: 700 11px/1 ui-monospace, Menlo, Consolas, monospace; letter-spacing: 1px; text-transform: uppercase; color: #e8e2ff; background: rgba(20, 10, 40, 0.85); border: 2px solid #7455dd; border-radius: 0; padding: 4px 8px; cursor: pointer; }
.resource-graph-overlay-action:hover, .resource-graph-overlay-action:focus-visible { background: #512bd4; outline: none; }
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

function isEditable(target) {
    return target instanceof Element && target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]') !== null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Overlay
// ---------------------------------------------------------------------------------------------------------------------

let activeOverlay = null;

class GraphOverlay {
    constructor(graph, svg, container, onClosed) {
        this.graph = graph;
        this.svg = svg;
        this.container = container;
        this.onClosed = onClosed;
        this.reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
        this.closed = false;

        const rect = container.getBoundingClientRect();
        const pixelScale = clamp(Math.round(rect.width / 420), 2, 4);
        this.width = Math.max(160, Math.floor(rect.width / pixelScale));
        this.height = Math.max(160, Math.floor(rect.height / pixelScale));

        // Read the graph before hiding it: positions, icons and colors come from the rendered nodes.
        this.roster = collectRoster(container);
        this.createOverlay();
        if (this.roster.length === 0) {
            this.roster.push({
                id: 'aspire',
                label: 'NO RESOURCES',
                color: '#b9aaee',
                fill: '#1b1a22',
                sprite: bitmapSprite(FALLBACK_ROWS, '#b9aaee'),
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

        this.sound = new AudioCues(readMuted(getLocalStorage));
        this.updateMuteButton();
        this.record = readRecord(getLocalStorage);
        this.marker = pathSprite(ASPIRE_LOGO, 17, 15, ASPIRE_LOGO_BOX);
        this.unitIcon = pathSprite(ASPIRE_LOGO, 8, 7, ASPIRE_LOGO_BOX);
        this.beaconSprite = bitmapSprite(BEACON_ROWS, '#ff5fd2');
        this.sparkSprite = bitmapSprite(SPARK_ROWS, '#ffffff');
        this.stars = Array.from({ length: 60 }, () => ({ x: Math.random() * this.width, y: Math.random() * this.height, phase: Math.random() * TAU }));

        this.keys = { left: false, right: false, act: false };
        this.pointerX = null;
        this.pointerAct = false;
        this.particles = [];
        this.texts = [];
        this.flashes = [];
        this.shake = 0;
        this.clock = 0;

        this.resetSession();
        this.state = 'morph-in';
        this.stateTime = 0;
        this.morphInDuration = this.reducedMotion
            ? REDUCED_MORPH
            : MORPH_PIXELATE + (Math.min(this.items.length, 30) * MORPH_STAGGER) + MORPH_FLIGHT;

        this.addListeners();
        this.root.focus({ preventScroll: true });
        this.lastFrame = performance.now();
        this.frameHandle = requestAnimationFrame(this.frame);
    }

    // --- DOM ---------------------------------------------------------------------------------------------------------

    createOverlay() {
        injectStyles();
        const root = document.createElement('div');
        root.className = 'resource-graph-overlay';
        root.tabIndex = -1;
        root.setAttribute('role', 'application');
        root.setAttribute('aria-label', LABELS.rootLabel);

        const canvas = createCanvas(this.width, this.height);
        const crt = document.createElement('div');
        crt.className = 'resource-graph-overlay-scan';

        const buttons = document.createElement('div');
        buttons.className = 'resource-graph-overlay-actions';
        const mute = document.createElement('button');
        mute.type = 'button';
        mute.className = 'resource-graph-overlay-action';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'resource-graph-overlay-action';
        close.textContent = 'Exit';
        close.setAttribute('aria-label', LABELS.exitLabel);
        buttons.append(mute, close);

        root.append(canvas, crt, buttons);
        this.container.appendChild(root);

        this.root = root;
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;
        this.crt = crt;
        this.buttons = buttons;
        this.muteButton = mute;
        this.closeButton = close;
    }

    hideGraph() {
        this.controls = this.container.querySelector('.resource-graph-controls');
        // The whole style attribute is saved (null when absent) so exiting leaves the elements exactly as they were.
        this.saved = {
            svgStyle: this.svg.getAttribute('style'),
            controlsStyle: this.controls?.getAttribute('style') ?? null,
        };
        this.svg.style.transition = 'none';
        this.svg.style.opacity = '0';
        if (this.controls) {
            this.controls.style.visibility = 'hidden';
        }
    }

    revealGraph(animate) {
        if (this.revealed) {
            return;
        }
        this.revealed = true;
        if (animate) {
            this.svg.style.transition = 'opacity 200ms ease-out';
            this.svg.style.opacity = '1';
        } else {
            restoreStyle(this.svg, this.saved.svgStyle);
        }
        if (this.controls) {
            restoreStyle(this.controls, this.saved.controlsStyle);
        }
    }

    updateMuteButton() {
        this.muteButton.textContent = this.sound.muted ? 'Sound: off' : 'Sound: on';
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
        window.addEventListener('keydown', this.onKeyDown, true);
        window.addEventListener('keyup', this.onKeyUp, true);
        window.addEventListener('blur', this.onBlur);
        document.addEventListener('visibilitychange', this.onVisibilityChange);
        this.root.addEventListener('focusout', this.onFocusOut);
        this.canvas.addEventListener('pointermove', this.onPointerMove);
        this.canvas.addEventListener('pointerdown', this.onPointerDown);
        window.addEventListener('pointerup', this.onPointerUp);
        this.canvas.addEventListener('contextmenu', this.onContextMenu);
        this.muteButton.addEventListener('click', this.onMuteClick);
        this.closeButton.addEventListener('click', this.onCloseClick);
    }

    removeListeners() {
        window.removeEventListener('keydown', this.onKeyDown, true);
        window.removeEventListener('keyup', this.onKeyUp, true);
        window.removeEventListener('blur', this.onBlur);
        document.removeEventListener('visibilitychange', this.onVisibilityChange);
        this.root.removeEventListener('focusout', this.onFocusOut);
        this.canvas.removeEventListener('pointermove', this.onPointerMove);
        this.canvas.removeEventListener('pointerdown', this.onPointerDown);
        window.removeEventListener('pointerup', this.onPointerUp);
        this.canvas.removeEventListener('contextmenu', this.onContextMenu);
        this.muteButton.removeEventListener('click', this.onMuteClick);
        this.closeButton.removeEventListener('click', this.onCloseClick);
    }

    // Keys are only taken while the overlay has focus, so the rest of the dashboard (search boxes, the resource list,
    // its keyboard shortcuts) keeps working when the user clicks away from it.
    ownsKeyboard(event) {
        if (isEditable(event.target)) {
            return false;
        }
        const active = document.activeElement;
        return active === null || active === document.body || this.root.contains(active);
    }

    onKeyDown = (event) => {
        if (this.state === 'morph-out' || !this.ownsKeyboard(event) || event.ctrlKey || event.metaKey || event.altKey) {
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
                this.sound.unlock();
                if (!event.repeat) {
                    this.keys.act = event.key === ' ';
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
            case 'Escape':
                this.quit();
                break;
            default:
                // Swallow other plain keys so single-key dashboard shortcuts don't run behind the overlay.
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
                this.keys.act = false;
                break;
            default:
                return;
        }
        if (this.ownsKeyboard(event)) {
            // Prevents Space from also activating a focused overlay button on key up.
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    };

    onBlur = () => {
        this.keys.left = this.keys.right = this.keys.act = false;
        this.pointerAct = false;
        this.pause();
    };

    onVisibilityChange = () => {
        if (document.hidden) {
            this.pause();
        }
    };

    onFocusOut = (event) => {
        if (!this.root.contains(event.relatedTarget)) {
            this.keys.left = this.keys.right = this.keys.act = false;
            if (event.relatedTarget) {
                this.pause();
            }
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
        this.pointerAct = true;
        this.primaryAction();
    };

    onPointerUp = () => {
        this.pointerAct = false;
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

    toggleMute() {
        this.sound.setMuted(!this.sound.muted);
        saveMuted(getLocalStorage, this.sound.muted);
        this.updateMuteButton();
    }

    // --- State -------------------------------------------------------------------------------------------------------

    setState(state) {
        this.state = state;
        this.stateTime = 0;
    }

    resetSession() {
        this.tally = 0;
        this.units = 3;
        this.stage = 1;
        this.newRecord = false;
        this.buildStage();
    }

    buildStage() {
        const layout = buildGrid(this.roster.length, this.stage);
        this.layout = layout;
        this.items = layout.slots.map(slot => ({
            resource: this.roster[slot.resourceIndex],
            row: slot.row,
            col: slot.col,
            alive: true,
            weight: itemWeight(slot.row, layout.rows),
        }));
        this.alive = this.items.length;

        const layoutWidth = ((layout.columns - 1) * SPACING_X) + SPRITE;
        this.baseX = Math.round((this.width - layoutWidth) / 2);
        this.baseY = HUD_HEIGHT + 14;
        this.offsetX = 0;
        // Each new stage starts a little lower.
        this.offsetY = Math.min(this.stage - 1, 5) * 4;
        this.direction = 1;
        this.legFrame = 0;
        this.stepTimer = stepInterval(this.alive, this.items.length, this.stage);

        this.anchor = { x: this.width / 2, y: this.height - 31, w: this.marker.width, h: this.marker.height, alive: true };
        this.probe = null;
        this.drops = [];
        this.dropTimer = 1.5;
        this.beacon = null;
        this.beaconTimer = 12 + (Math.random() * 10);
        this.sound?.beaconStop();

        const blockCount = this.width >= 300 ? 4 : 3;
        const spacing = this.width / blockCount;
        const blockY = this.anchor.y - 8 - BLOCK_ROWS.length;
        this.blocks = Array.from({ length: blockCount }, (_, i) =>
            createBlock(Math.round((spacing * (i + 0.5)) - (BLOCK_ROWS[0].length / 2)), blockY));
    }

    itemPosition(item) {
        return {
            x: Math.round(this.baseX + (item.col * SPACING_X) + this.offsetX),
            y: Math.round(this.baseY + (item.row * SPACING_Y) + this.offsetY),
        };
    }

    primaryAction() {
        switch (this.state) {
            case 'title':
                this.sound.start();
                this.setState('ready');
                break;
            case 'finished':
                this.resetSession();
                this.sound.start();
                this.setState('ready');
                break;
            case 'paused':
                this.togglePause();
                break;
            case 'active':
                this.emit();
                break;
        }
    }

    pause() {
        if (this.state === 'active' || this.state === 'ready') {
            this.pausedState = this.state;
            this.sound.beaconStop();
            this.state = 'paused';
        }
    }

    togglePause() {
        if (this.state === 'paused') {
            this.state = this.pausedState;
            if (this.beacon) {
                this.sound.beaconStart();
            }
        } else {
            this.pause();
        }
    }

    emit() {
        if (this.state !== 'active' || this.probe || !this.anchor.alive) {
            return;
        }
        this.probe = { x: Math.round(this.anchor.x), y: this.anchor.y - 4, w: 1, h: 5 };
        this.sound.ping();
    }

    addTally(points) {
        const previous = this.tally;
        this.tally += points;
        const earned = bonusUnits(previous, this.tally);
        if (earned > 0 && this.units < MAX_UNITS) {
            this.units = Math.min(MAX_UNITS, this.units + earned);
            this.addText(LABELS.unit, this.anchor.x, this.anchor.y - 10, '#7dffa0');
            this.sound.extraLife();
        }
    }

    quit() {
        if (this.state === 'morph-out' || this.closed) {
            return;
        }
        this.storeRecord();
        this.sound.beaconStop();
        this.sound.powerDown();

        // Fly one item per resource back to where its node is now. Resources may have moved or changed while open,
        // so node positions are read again; a resource that is gone flies off the top.
        const targets = new Map(collectRoster(this.container).map(entry => [entry.id, entry.screen]));
        const inGrid = this.state !== 'morph-in';
        this.outbound = this.roster.map(entry => {
            const item = this.items.find(a => a.resource === entry && a.alive) ?? this.items.find(a => a.resource === entry);
            let from = entry.from;
            if (item && inGrid) {
                const position = this.itemPosition(item);
                from = { x: position.x + (SPRITE / 2), y: position.y + (SPRITE / 2), r: SPRITE / 2 };
            }
            const screen = targets.get(entry.id);
            const to = screen ? this.screenToWorld(screen.x, screen.y, screen.r) : { x: from.x, y: -40, r: SPRITE / 2 };
            return { entry, item: inGrid ? item : null, from, to };
        });
        this.wasInGrid = inGrid;
        this.setState('morph-out');
    }

    storeRecord() {
        if (this.tally > this.record) {
            this.record = saveRecord(getLocalStorage, this.tally);
            this.newRecord = true;
        }
    }

    close() {
        if (this.closed) {
            return;
        }
        this.closed = true;
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
            this.container.focus({ preventScroll: true });
        }
        this.onClosed();
    }

    // --- Loop --------------------------------------------------------------------------------------------------------

    frame = (now) => {
        if (this.closed) {
            return;
        }
        // Blazor can hide (switch layout) or remove (navigate away) the graph while open. The graph can be hidden by
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
            case 'finished':
                this.legFrame = Math.floor(this.clock * 2) % 2;
                break;
            case 'ready':
                this.updateAnchor(dt);
                if (this.stateTime >= 1.4) {
                    this.setState('active');
                }
                break;
            case 'active':
                this.updateActive(dt);
                break;
            case 'lost':
                if (this.stateTime >= 1.6) {
                    if (this.units > 0) {
                        this.anchor.alive = true;
                        this.anchor.x = this.width / 2;
                        this.drops = [];
                        this.setState('active');
                    } else {
                        this.finish();
                    }
                }
                break;
            case 'cleared':
                if (this.stateTime >= 2.2) {
                    this.stage++;
                    this.buildStage();
                    this.setState('ready');
                }
                break;
        }

        if (this.state !== 'paused') {
            this.updateEffects(dt);
        }
    }

    updateAnchor(dt) {
        const anchor = this.anchor;
        if (!anchor.alive) {
            return;
        }
        const half = anchor.w / 2;
        if (this.keys.left !== this.keys.right) {
            anchor.x += (this.keys.left ? -1 : 1) * 120 * dt;
        } else if (this.pointerX !== null) {
            const delta = this.pointerX - anchor.x;
            anchor.x += clamp(delta, -240 * dt, 240 * dt);
        }
        anchor.x = clamp(anchor.x, half + 2, this.width - half - 2);
    }

    updateActive(dt) {
        this.updateAnchor(dt);
        if (this.keys.act || this.pointerAct) {
            this.emit();
        }

        this.updateProbe(dt);
        if (this.state !== 'active') {
            return;
        }
        this.updateGrid(dt);
        if (this.state !== 'active') {
            return;
        }
        this.updateDrops(dt);
        this.updateBeacon(dt);

        if (this.alive === 0 && this.state === 'active') {
            this.addTally(50 * this.stage);
            this.sound.beaconStop();
            this.beacon = null;
            this.drops = [];
            this.probe = null;
            this.sound.stageClear();
            this.setState('cleared');
        }
    }

    updateProbe(dt) {
        const probe = this.probe;
        if (!probe) {
            return;
        }
        probe.y -= 260 * dt;
        if (probe.y + probe.h < HUD_HEIGHT) {
            this.probe = null;
            return;
        }

        for (const item of this.items) {
            if (!item.alive) {
                continue;
            }
            const p = this.itemPosition(item);
            if (rectsOverlap(probe, { x: p.x, y: p.y, w: SPRITE, h: SPRITE + 2 })) {
                this.removeItem(item, p);
                this.probe = null;
                return;
            }
        }

        if (this.beacon && rectsOverlap(probe, { x: this.beacon.x, y: this.beacon.y, w: this.beaconSprite.width, h: this.beaconSprite.height })) {
            const points = pick(BEACON_VALUES);
            this.addTally(points);
            this.scatter(this.beaconSprite, this.beacon.x, this.beacon.y, 1.4);
            this.addText(`+${points} ${pick(BEACON_LABELS)}`, this.beacon.x + (this.beaconSprite.width / 2), this.beacon.y + 10, '#ff5fd2');
            this.sound.beaconStop();
            this.sound.beaconHit();
            this.beacon = null;
            this.probe = null;
            return;
        }

        for (let i = 0; i < this.drops.length; i++) {
            if (rectsOverlap(probe, this.drops[i])) {
                this.sparkAt(probe.x, probe.y);
                this.drops.splice(i, 1);
                this.probe = null;
                return;
            }
        }

        for (const block of this.blocks) {
            const hit = blockCollision(block, probe);
            if (hit) {
                erodeBlock(block, hit.x, hit.y, 2.5);
                this.probe = null;
                return;
            }
        }
    }

    removeItem(item, position) {
        item.alive = false;
        this.alive--;
        this.addTally(item.weight);
        this.scatter(item.resource.sprite, position.x, position.y, 1);
        this.flashes.push({ x: position.x, y: position.y, life: 0.12 });
        const name = shorten(item.resource.label, 12);
        this.addText(`${name}: ${pick(EXIT_STATES)}`, position.x + (SPRITE / 2), position.y, item.resource.color);
        this.sound.burst();
    }

    updateGrid(dt) {
        this.stepTimer -= dt;
        if (this.stepTimer > 0) {
            return;
        }
        this.stepTimer += stepInterval(this.alive, this.items.length, this.stage);

        let minX = Infinity;
        let maxX = -Infinity;
        for (const item of this.items) {
            if (item.alive) {
                const p = this.itemPosition(item);
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
        this.sound.pulse();

        // The grid erases blocks it moves through and ends the session if it reaches the marker's row.
        for (const item of this.items) {
            if (!item.alive) {
                continue;
            }
            const p = this.itemPosition(item);
            const rect = { x: p.x, y: p.y, w: SPRITE, h: SPRITE + 2 };
            for (const block of this.blocks) {
                if (rectsOverlap(rect, block)) {
                    clearBlock(block, rect);
                }
            }
            if (rect.y + rect.h >= this.anchor.y) {
                this.units = 0;
                this.loseAnchor(LABELS.overrun);
                return;
            }
        }
    }

    updateDrops(dt) {
        this.dropTimer -= dt;
        const maxDrops = Math.min(8, 2 + this.stage);
        if (this.dropTimer <= 0 && this.drops.length < maxDrops) {
            this.dropTimer = Math.max(0.25, 1.3 * Math.pow(0.85, this.stage - 1)) * (0.5 + Math.random());
            this.spawnDrop();
        }

        const speed = Math.min(130, 60 + (this.stage * 6));
        const groundY = this.height - 12;
        for (let i = this.drops.length - 1; i >= 0; i--) {
            const drop = this.drops[i];
            drop.y += speed * dt;
            drop.frame += dt * 10;
            let remove = false;
            if (drop.y + drop.h >= groundY) {
                remove = true;
                this.sparkAt(drop.x + 1, groundY - 1);
            } else if (this.anchor.alive && rectsOverlap(drop, { x: this.anchor.x - (this.anchor.w / 2) + 2, y: this.anchor.y + 3, w: this.anchor.w - 4, h: this.anchor.h - 3 })) {
                this.drops.splice(i, 1);
                this.loseAnchor(null);
                return;
            } else {
                for (const block of this.blocks) {
                    const hit = blockCollision(block, drop);
                    if (hit) {
                        erodeBlock(block, hit.x, hit.y + 1, 2.5);
                        remove = true;
                        break;
                    }
                }
            }
            if (remove) {
                this.drops.splice(i, 1);
            }
        }
    }

    spawnDrop() {
        // The bottom item of a random column spawns the drop; a third of the time it's the column above the marker.
        const bottoms = new Map();
        for (const item of this.items) {
            if (item.alive) {
                const current = bottoms.get(item.col);
                if (!current || item.row > current.row) {
                    bottoms.set(item.col, item);
                }
            }
        }
        const candidates = [...bottoms.values()];
        if (candidates.length === 0) {
            return;
        }
        let origin = pick(candidates);
        if (Math.random() < 0.33) {
            let record = Infinity;
            for (const item of candidates) {
                const distance = Math.abs(this.itemPosition(item).x + (SPRITE / 2) - this.anchor.x);
                if (distance < record) {
                    record = distance;
                    origin = item;
                }
            }
        }
        const p = this.itemPosition(origin);
        this.drops.push({ x: p.x + (SPRITE / 2) - 1, y: p.y + SPRITE + 2, w: 3, h: 7, frame: 0 });
    }

    updateBeacon(dt) {
        if (this.beacon) {
            this.beacon.x += this.beacon.direction * (40 + (this.stage * 3)) * dt;
            if (this.beacon.x < -this.beaconSprite.width - 2 || this.beacon.x > this.width + 2) {
                this.beacon = null;
                this.sound.beaconStop();
            }
            return;
        }
        this.beaconTimer -= dt;
        if (this.beaconTimer <= 0 && this.alive >= 4) {
            this.beaconTimer = 18 + (Math.random() * 12);
            const direction = Math.random() < 0.5 ? 1 : -1;
            this.beacon = { x: direction > 0 ? -this.beaconSprite.width : this.width, y: HUD_HEIGHT + 2, direction };
            this.sound.beaconStart();
        }
    }

    loseAnchor(message) {
        const anchor = this.anchor;
        anchor.alive = false;
        this.units = Math.max(0, this.units - 1);
        this.scatter(this.marker, anchor.x - (anchor.w / 2), anchor.y, 2);
        this.addText(message ?? (this.units > 0 ? LABELS.restart : LABELS.stopped), anchor.x, anchor.y - 8, '#ffffff');
        this.shake = this.reducedMotion ? 0 : 0.5;
        this.probe = null;
        this.sound.beaconStop();
        this.beacon = null;
        this.sound.anchorLost();
        // The particles play before the marker comes back or the session ends.
        this.setState('lost');
    }

    finish() {
        this.newRecord = false;
        this.storeRecord();
        this.finishLabel = pick(FINISH_LABELS);
        this.sound.finish();
        this.setState('finished');
    }

    // --- Effects -----------------------------------------------------------------------------------------------------

    scatter(sprite, x, y, force) {
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

    renderItem(item, x, y, alpha = 1) {
        const ctx = this.ctx;
        const sprite = item.resource.sprite;
        ctx.globalAlpha = alpha;
        ctx.drawImage(sprite.canvas, x, y);
        ctx.fillStyle = item.resource.color;
        for (const [lx, ly] of LEGS[this.legFrame]) {
            ctx.fillRect(x + lx, y + SPRITE + ly, 1, 1);
        }
        ctx.globalAlpha = 1;
    }

    renderWorld() {
        const ctx = this.ctx;

        // HUD.
        drawText(ctx, `${LABELS.hudLeft} ${padTally(this.tally)}`, 4, 4, '#ffffff');
        drawText(ctx, `${LABELS.hudCenter} ${padTally(Math.max(this.record, this.tally))}`, this.width / 2, 4, '#b9aaee', 1, 'center');
        drawText(ctx, `${LABELS.hudRight} ${this.stage}`, this.width - 4, 4, '#ffffff', 1, 'right');

        for (const block of this.blocks) {
            this.renderBlock(block);
        }

        // During "ready" the grid slides into place.
        let dropY = 0;
        let itemAlpha = 1;
        if (this.state === 'ready') {
            const p = easeInOutCubic(clamp(this.stateTime / 0.9, 0, 1));
            dropY = Math.round(-(1 - p) * 60);
            itemAlpha = p;
        }
        for (const item of this.items) {
            if (item.alive) {
                const p = this.itemPosition(item);
                this.renderItem(item, p.x, p.y + dropY, itemAlpha);
            }
        }

        for (const flash of this.flashes) {
            ctx.drawImage(this.sparkSprite.canvas, flash.x, flash.y);
        }

        if (this.beacon) {
            ctx.drawImage(this.beaconSprite.canvas, Math.round(this.beacon.x), this.beacon.y);
        }

        const anchor = this.anchor;
        if (anchor.alive && (this.state !== 'ready' || Math.floor(this.stateTime * 8) % 2 === 0)) {
            ctx.drawImage(this.marker.canvas, Math.round(anchor.x - (anchor.w / 2)), anchor.y);
        }

        if (this.probe) {
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(this.probe.x, Math.round(this.probe.y), this.probe.w, this.probe.h);
        }

        ctx.fillStyle = '#ffd36b';
        for (const drop of this.drops) {
            for (const [bx, by] of DROP_FRAMES[Math.floor(drop.frame) % 2]) {
                ctx.fillRect(Math.round(drop.x) + bx, Math.round(drop.y) + by, 1, 1);
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

        // Ground and remaining units.
        const groundY = this.height - 12;
        ctx.fillStyle = '#7455dd';
        ctx.fillRect(0, groundY, this.width, 1);
        for (let i = 0; i < this.units; i++) {
            ctx.drawImage(this.unitIcon.canvas, 4 + (i * 10), groundY + 3);
        }
    }

    renderBlock(block) {
        if (block.dirty || !block.canvas) {
            block.canvas ??= createCanvas(block.w, block.h);
            const ctx = block.canvas.getContext('2d');
            ctx.clearRect(0, 0, block.w, block.h);
            ctx.fillStyle = '#3dff8a';
            for (let row = 0; row < block.h; row++) {
                for (let col = 0; col < block.w; col++) {
                    if (block.cells[(row * block.w) + col]) {
                        ctx.fillRect(col, row, 1, 1);
                    }
                }
            }
            block.dirty = false;
        }
        this.ctx.drawImage(block.canvas, block.x, block.y);
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

    renderOverlay() {
        const ctx = this.ctx;
        const cx = this.width / 2;
        const blink = Math.floor(this.clock * 2.5) % 2 === 0;

        switch (this.state) {
            case 'title': {
                const shown = this.roster.slice(0, 5);
                const height = 114 + (shown.length * 14) + (this.roster.length > shown.length ? 10 : 0);
                const top = Math.max(HUD_HEIGHT + 2, Math.round((this.height - height) / 2));
                this.renderPanel(top, height);
                drawText(ctx, LABELS.titleTop, cx, top + 8, '#dcd5f6', 2, 'center');
                drawText(ctx, LABELS.titleBottom, cx, top + 26, '#9780e5', 2, 'center');
                drawText(ctx, `${LABELS.recordLine} ${padTally(this.record)}`, cx, top + 46, '#ffffff', 1, 'center');

                let y = top + 60;
                const left = Math.round(cx - 70);
                for (const entry of shown) {
                    ctx.drawImage(entry.sprite.canvas, left, y - 2);
                    drawText(ctx, shorten(entry.label, 20), left + 18, y, entry.color);
                    y += 14;
                }
                if (this.roster.length > shown.length) {
                    drawText(ctx, format(LABELS.more, this.roster.length - shown.length), left + 18, y, '#b9aaee');
                    y += 10;
                }
                ctx.drawImage(this.beaconSprite.canvas, left - 2, y);
                drawText(ctx, LABELS.beacon, left + 18, y, '#ff5fd2');
                y += 16;
                if (blink) {
                    drawText(ctx, LABELS.start, cx, y, '#7dffa0', 1, 'center');
                }
                drawText(ctx, LABELS.help1, cx, y + 14, '#8a80b0', 1, 'center');
                drawText(ctx, LABELS.help2, cx, y + 24, '#8a80b0', 1, 'center');
                break;
            }
            case 'ready':
                drawText(ctx, `${LABELS.hudRight} ${this.stage}`, cx, Math.round(this.height * 0.55), '#ffffff', 2, 'center');
                drawText(ctx, this.stage === 1 ? LABELS.stageFirst : LABELS.stageNext, cx, Math.round(this.height * 0.55) + 20, '#b9aaee', 1, 'center');
                break;
            case 'paused':
                this.renderPanel(Math.round(this.height / 2) - 20, 40);
                drawText(ctx, LABELS.paused, cx, Math.round(this.height / 2) - 12, '#ffffff', 2, 'center');
                drawText(ctx, LABELS.resume, cx, Math.round(this.height / 2) + 6, '#b9aaee', 1, 'center');
                break;
            case 'cleared':
                drawText(ctx, format(LABELS.cleared, this.stage), cx, Math.round(this.height * 0.45), '#7dffa0', 2, 'center');
                drawText(ctx, LABELS.clearedNote, cx, Math.round(this.height * 0.45) + 20, '#ffffff', 1, 'center');
                drawText(ctx, format(LABELS.bonus, 50 * this.stage), cx, Math.round(this.height * 0.45) + 32, '#ffd36b', 1, 'center');
                break;
            case 'finished': {
                const top = Math.round((this.height - 100) / 2);
                this.renderPanel(top, 100);
                drawText(ctx, LABELS.finished, cx, top + 8, '#ff5f5f', 2, 'center');
                drawText(ctx, this.finishLabel, cx, top + 28, '#b9aaee', 1, 'center');
                drawText(ctx, `${LABELS.tallyLine} ${padTally(this.tally)}`, cx, top + 42, '#ffffff', 1, 'center');
                drawText(ctx, `${LABELS.recordLine} ${padTally(this.record)}`, cx, top + 52, '#ffffff', 1, 'center');
                if (this.newRecord && blink) {
                    drawText(ctx, LABELS.newRecord, cx, top + 64, '#ffd36b', 1, 'center');
                }
                if (this.stateTime > 1) {
                    drawText(ctx, LABELS.again, cx, top + 78, '#7dffa0', 1, 'center');
                    drawText(ctx, LABELS.back, cx, top + 88, '#8a80b0', 1, 'center');
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
            for (const item of this.items) {
                const position = this.itemPosition(item);
                this.renderItem(item, position.x, position.y, p);
            }
            return;
        }

        this.renderLinks(1 - clamp(ms / MORPH_PIXELATE, 0, 1), this.roster.map(entry => entry.from));

        // The first item of each resource is the node itself; the copies split off it one after another.
        const started = new Set();
        this.items.forEach((item, index) => {
            const entry = item.resource;
            const isOriginal = !started.has(entry);
            started.add(entry);
            const delay = MORPH_PIXELATE + (Math.min(index, 30) * MORPH_STAGGER);
            const p = easeInOutCubic(clamp((ms - delay) / MORPH_FLIGHT, 0, 1));
            if (p <= 0 && !isOriginal) {
                return;
            }

            const slot = this.itemPosition(item);
            const to = { x: slot.x + (SPRITE / 2), y: slot.y + (SPRITE / 2) };
            const from = entry.from;
            // Fly along a curve that bulges upward and alternates sideways, so items don't move in straight lines.
            const control = { x: ((from.x + to.x) / 2) + ((index % 2 === 0 ? 1 : -1) * 24), y: Math.min(from.y, to.y) - 40 };
            const q = 1 - p;
            const x = (q * q * from.x) + (2 * q * p * control.x) + (p * p * to.x);
            const y = (q * q * from.y) + (2 * q * p * control.y) + (p * p * to.y);
            const radius = lerp(from.r, SPRITE * 0.6, p);
            const scale = lerp(this.nodeSpriteScale(from.r), 1, p);
            this.renderNode(entry, x, y, radius, scale, isOriginal ? 1 - p : 0, isOriginal ? 1 : clamp(p * 3, 0, 1));
            if (p >= 1) {
                this.renderLegs(item, slot.x, slot.y);
            }
        });
    }

    renderLegs(item, x, y) {
        const ctx = this.ctx;
        ctx.fillStyle = item.resource.color;
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
        // Copies fade out where they are while one item per resource flies home.
        if (this.wasInGrid) {
            const copiesAlpha = 1 - clamp(ms / 300, 0, 1);
            if (copiesAlpha > 0) {
                const flying = new Set(this.outbound.map(flight => flight.item));
                for (const item of this.items) {
                    if (item.alive && !flying.has(item)) {
                        const position = this.itemPosition(item);
                        this.renderItem(item, position.x, position.y, copiesAlpha);
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
// Gesture
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Listens for the gesture on the graph. This is the only thing that runs while the overlay is closed: a
 * passive pointermove listener that keeps a short trail of hover positions over empty graph space.
 */
export function attachGraphInteractions(graph) {
    const svg = typeof graph?.svg?.node === 'function' ? graph.svg.node() : null;
    if (!svg || typeof svg.addEventListener !== 'function' || svg.__aspireGraphInteractions) {
        return;
    }

    let points = [];
    let sinceCheck = 0;

    const onPointerMove = (event) => {
        // Only hover with no button pressed counts: panning and dragging nodes always hold a button. Pointer over a
        // node resets the trail so the circles have to be drawn over empty space.
        if (activeOverlay || event.buttons !== 0 || event.pointerType === 'touch' ||
            (event.target instanceof Element && event.target.closest('.resource-group'))) {
            points = [];
            return;
        }

        const now = event.timeStamp;
        const last = points[points.length - 1];
        if (last && now - last.t < 12) {
            return;
        }
        points.push({ x: event.clientX, y: event.clientY, t: now });
        while (points.length > 0 && now - points[0].t > DEFAULT_GESTURE.windowMs) {
            points.shift();
        }

        if (++sinceCheck >= 3) {
            sinceCheck = 0;
            if (detectCircleGesture(points)) {
                points = [];
                openOverlay(graph, svg);
            }
        }
    };
    const onPointerLeave = () => {
        points = [];
    };

    svg.addEventListener('pointermove', onPointerMove, { passive: true });
    svg.addEventListener('pointerleave', onPointerLeave, { passive: true });
    svg.__aspireGraphInteractions = true;
}

function openOverlay(graph, svg) {
    if (activeOverlay) {
        return;
    }
    const container = svg.closest('.resource-graph-container') ?? svg.parentElement;
    if (!container || !isShown(container)) {
        return;
    }
    activeOverlay = new GraphOverlay(graph, svg, container, () => {
        activeOverlay = null;
    });
}

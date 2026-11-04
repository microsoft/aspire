// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import '../../js/d3.v7.min.js';

const observations = new WeakMap();
// Snap node geometry to four pixels; routing adds separate coordinates between obstacles and ports.
const grid = 4;
const snap = value => Math.round(value / grid) * grid;

export function observe(element, receiver) {
    let timer;
    const observer = new ResizeObserver(entries => {
        if (timer !== undefined) return;
        timer = setTimeout(() => {
            timer = undefined;
            receiver.invokeMethodAsync('ResizeAsync', Math.floor(element.clientWidth)).catch(error => {
                if (observations.has(element)) console.error('Could not resize the relationship graph.', error);
            });
        }, 100);
    });
    let hovered, focused;
    const highlight = target => {
        const edge = target?.closest('[data-edge]');
        const node = target?.closest('[data-node]');
        const name = node?.dataset.node;
        const active = edge?.dataset.edge;
        const enabled = active !== undefined || (name !== undefined && !node.classList.contains('center'));
        element.classList.toggle('has-highlight', enabled);
        for (const group of element.querySelectorAll('.relationship-edge')) {
            group.classList.toggle('active', enabled && (group.dataset.edge === active ||
                group.dataset.source === name || group.dataset.target === name));
        }
    };
    const pointerOver = event => {
        hovered = event.target;
        if (!focused) highlight(hovered);
    };
    const pointerLeave = () => {
        hovered = undefined;
        if (!focused) highlight();
    };
    const focusIn = event => {
        focused = event.target.closest('.relationship-graph-edge, .relationship-graph-node');
        // Native SVG anchor focus does not consistently reveal links inside an overflowing SVG viewport.
        if (focused?.matches('a[href]')) focused.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        highlight(focused);
    };
    const focusOut = () => {
        focused = undefined;
        highlight(hovered);
    };
    observer.observe(element);
    element.addEventListener('pointerover', pointerOver);
    element.addEventListener('focusin', focusIn);
    element.addEventListener('pointerleave', pointerLeave);
    element.addEventListener('focusout', focusOut);
    observations.set(element, () => {
        observer.disconnect();
        clearTimeout(timer);
        element.removeEventListener('pointerover', pointerOver);
        element.removeEventListener('focusin', focusIn);
        element.removeEventListener('pointerleave', pointerLeave);
        element.removeEventListener('focusout', focusOut);
    });
}

export function disconnect(element) {
    observations.get(element)?.();
    observations.delete(element);
}

export async function center(element) {
    const focused = document.activeElement;
    if (element.contains(focused) && focused.matches('.relationship-graph-node[href]')) {
        focused.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    } else {
        const viewport = element.querySelector('.relationship-viewport');
        const selected = element.querySelector('.center rect');
        if (viewport && selected) {
            const box = selected.getBBox();
            const graph = element.querySelector('.relationship-graph');
            viewport.scrollLeft = graph.dataset.layout === 'Flow'
                ? Math.max(0, box.x - 16)
                : Math.max(0, box.x + box.width / 2 - viewport.clientWidth / 2);
        }
    }
    await new Promise(resolve => requestAnimationFrame(resolve));
    const graph = element.querySelector('.relationship-graph');
    graph.dataset.layoutReady = graph.dataset.layout;
}

export function layout(element, mode, width, resource, incoming, outgoing) {
    const started = performance.now();
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('2d');
    // Measure using the selected node's heavier font so neither selected nor ordinary labels overflow.
    context.font = `600 12px ${getComputedStyle(element).fontFamily}`;
    const result = createLayout(mode, width, resource, incoming, outgoing, text => context.measureText(text).width);
    element.dataset.layoutDurationMs = (performance.now() - started).toFixed(1);
    // Large graphs exceed Blazor Server's single-message limit. Stream only the render model,
    // not the intermediate routing points used by geometry checks.
    // https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/?view=aspnetcore-10.0#size-limits-on-javascript-interop-calls
    const renderModel = { ...result, edges: result.edges.map(({ points, ...edge }) => edge) };
    return new TextEncoder().encode(JSON.stringify(renderModel));
}

// D3 also backs the application's resource graph. These layouts remain static: no simulation timer, pan or zoom.
export function createLayout(mode, availableWidth, resource, incoming, outgoing, measure) {
    const connections = [...incoming.map(node => ({ node, incoming: true })),
        ...outgoing.map(node => ({ node, incoming: false }))];
    const distinct = new Map();
    for (const connection of connections) {
        if (!distinct.has(connection.node.name)) distinct.set(connection.node.name, connection);
    }
    const unique = [...distinct.values()];
    const edgesByName = new Map(unique.map(connection => [connection.node.name, []]));
    for (const [i, connection] of connections.entries()) edgesByName.get(connection.node.name).push({ ...connection, id: String(i) });
    const rankCount = 1 + Number(unique.some(connection => connection.incoming)) +
        Number(unique.some(connection => !connection.incoming));
    // Budget all occupied Flow ranks before falling back to overflow. Labels retain their normal size and wrap.
    const widthBudget = (availableWidth - 32 - (rankCount - 1) * 40) / rankCount;
    const contentWidth = Math.ceil((Math.max(...[resource, ...unique.map(connection => connection.node)]
        .map(node => measure(node.name))) + 36) / 8) * 8;
    const nodeWidth = Math.min(Math.max(128, contentWidth), 240, Math.max(128, Math.floor(widthBudget / 8) * 8));
    const wrap = (name, width = nodeWidth) => {
        const lines = [];
        let line = '';
        for (const character of name) {
            while (line && measure(line + character) > width - 36) {
                const separator = Math.max(line.lastIndexOf('-'), line.lastIndexOf('.'), line.lastIndexOf('_'));
                if (separator >= 0) {
                    lines.push(line.slice(0, separator + 1));
                    line = line.slice(separator + 1);
                } else {
                    lines.push(line);
                    line = '';
                }
            }
            line += character;
        }
        lines.push(line);
        return lines;
    };
    const nodes = [resource, ...unique.map(connection => connection.node)].map((node, i) => ({
        node, isCenter: i === 0, x: 0, y: 0, width: nodeWidth,
        lines: wrap(node.name), height: 64
    }));
    const height = Math.max(56, ...nodes.map(node => Math.ceil((node.lines.length * 16 + 24) / 8) * 8));
    for (const node of nodes) node.height = height;
    const selected = nodes[0];
    const byName = new Map(nodes.map(node => [node.node.name, node]));
    const row = height + 12;
    if (mode === 'Flow') {
        const remaining = rankCount > 1 ? (availableWidth - rankCount * nodeWidth - 32) / (rankCount - 1) : 40;
        const gap = Math.floor(Math.max(40, Math.min(136, remaining)) / grid) * grid;
        for (const isIncoming of [true, false]) {
            const neighbors = unique.filter(connection => connection.incoming === isIncoming);
            const tree = d3.tree().nodeSize([row, nodeWidth + gap])(d3.hierarchy({ children: neighbors }));
                const offset = Math.max(0, (selected.height - (neighbors.length * row - 12)) / 2);
            for (const leaf of tree.leaves()) {
                if (!leaf.data.node) continue;
                const node = byName.get(leaf.data.node.name);
                node.x = (isIncoming ? -1 : 1) * (nodeWidth + gap);
                // Start each rank at the selected resource instead of opening a narrow viewport on empty space.
                node.y = leaf.x - Math.min(...tree.leaves().map(item => item.x)) + offset;
            }
        }
    } else if (mode === 'Hierarchy') {
        const top = unique.filter(connection => connection.incoming !== (connection.node.type === 'Parent'));
        const bottom = unique.filter(connection => !top.includes(connection));
        // Direct peers share one rank. Folding them into rows invents levels and forces paths around peers.
        // Preserve readable node sizes and let the existing bounded viewport scroll across a wide rank.
        for (const [neighbors, direction] of [[top, -1], [bottom, 1]]) {
            const names = new Set(neighbors.map(connection => connection.node.name));
            const count = connections.filter(connection => names.has(connection.node.name)).length;
            // Give a wide set of 16px ports room to fan out, without oversized gutters for a small rank.
            const gap = Math.max(96, (count - 1) * 8 + 56);
            const tree = d3.tree().nodeSize([nodeWidth + 32, height + gap])(d3.hierarchy({ children: neighbors }));
            for (const leaf of tree.leaves()) {
                if (!leaf.data.node) continue;
                const node = byName.get(leaf.data.node.name);
                node.x = leaf.x + (selected.width - node.width) / 2;
                node.y = direction * leaf.y;
            }
        }
    } else {
        // A 32px target neck and 16px source escape still need a free approach lane between boxes.
        const clearance = 64;
        const points = nodes.map((node, i) => ({
            node, x: i === 0 ? 0 : Math.cos(i * 2.4) * 280,
            y: i === 0 ? 0 : Math.sin(i * 2.4) * 180,
            ...(i === 0 ? { fx: 0, fy: 0 } : {})
        }));
        const collide = () => {
            for (let i = 0; i < points.length; i++) for (let j = i + 1; j < points.length; j++) {
                const a = points[i], b = points[j];
                const dx = a.x + a.node.width / 2 - b.x - b.node.width / 2;
                const dy = a.y + a.node.height / 2 - b.y - b.node.height / 2;
                const overlapX = (a.node.width + b.node.width) / 2 + clearance - Math.abs(dx);
                const overlapY = (a.node.height + b.node.height) / 2 + clearance - Math.abs(dy);
                if (overlapX <= 0 || overlapY <= 0) continue;
                const share = i === 0 ? 1 : .5;
                if (overlapX < overlapY) {
                    const move = Math.sign(dx || 1) * overlapX;
                    if (i !== 0) a.x += move * share;
                    b.x -= move * share;
                } else {
                    const move = Math.sign(dy || 1) * overlapY;
                    if (i !== 0) a.y += move * share;
                    b.y -= move * share;
                }
            }
        };
        const simulation = d3.forceSimulation(points).stop().randomSource(d3.randomLcg(42))
            .force('x', d3.forceX(0).strength(.045))
            .force('y', d3.forceY(point => (selected.height - point.node.height) / 2).strength(.12))
            .force('charge', d3.forceManyBody().strength(-120))
            .force('collide', collide);
        simulation.tick(300);
        let resized;
        do {
            for (const point of points) {
                point.node.x = snap(point.x);
                point.node.y = snap(point.y);
            }
            // A fixed number of force/collision passes does not prove separation at high degree.
            // Project each candidate to the nearest free vertical interval, keeping its D3-selected column.
            const placed = [selected];
            for (const point of points.slice(1)) {
                const node = point.node;
                const intervals = placed.filter(other =>
                    node.x < other.x + other.width + clearance && node.x + node.width + clearance > other.x)
                    .map(other => [other.y - node.height - clearance, other.y + other.height + clearance])
                    .sort((a, b) => a[0] - b[0]);
                const merged = [];
                for (const interval of intervals) {
                    if (merged.length && interval[0] <= merged.at(-1)[1]) {
                        merged.at(-1)[1] = Math.max(merged.at(-1)[1], interval[1]);
                    } else {
                        merged.push(interval);
                    }
                }
                const blocked = merged.find(([start, end]) => node.y > start && node.y < end);
                if (blocked) {
                    node.y = node.y - blocked[0] <= blocked[1] - node.y ? blocked[0] : blocked[1];
                    point.y = node.y;
                }
                placed.push(node);
            }
            resized = false;
            for (const point of points) {
                const node = point.node;
                const edges = node.isCenter ? connections.map((edge, i) => ({ ...edge, id: String(i) }))
                    : edgesByName.get(node.node.name);
                const targetFor = edge => node.isCenter ? byName.get(edge.node.name) : selected;
                for (const [face, ports] of groupPorts(node, edges, targetFor, mode)) {
                    const property = face === 'left' || face === 'right' ? 'height' : 'width';
                    const required = Math.ceil((ports.at(-1).offset + 32) / grid) * grid;
                    if (required <= node[property]) continue;
                    // Face populations depend on actual placement, not total degree / 2. Grow monotonically
                    // and settle collisions again before routing; this also handles reciprocal/parallel types.
                    const delta = required - node[property];
                    node[property] = required;
                    const axis = property === 'width' ? 'x' : 'y';
                    point[axis] -= delta / 2;
                    if (node.isCenter) point[axis === 'x' ? 'fx' : 'fy'] = point[axis];
                    resized = true;
                }
            }
            if (resized) {
                simulation.alpha(.3).tick(64);
            }
        } while (resized);
    }
    if (mode !== 'Topology') {
        for (const node of nodes) {
            const edges = node.isCenter ? connections.map((edge, i) => ({ ...edge, id: String(i) }))
                : edgesByName.get(node.node.name);
            const targetFor = edge => node.isCenter ? byName.get(edge.node.name) : selected;
            for (const [face, ports] of groupPorts(node, edges, targetFor, mode)) {
                const property = face === 'left' || face === 'right' ? 'height' : 'width';
                node[property] = Math.max(node[property], Math.ceil((ports.at(-1).offset + 32) / grid) * grid);
            }
        }
    }
    const left = Math.min(...nodes.map(node => node.x));
    const top = Math.min(...nodes.map(node => node.y));
    // Reserve room for a different lane per connection, including outside the outermost rank.
    const padding = Math.ceil(Math.max(48, connections.length * 8) / 8) * 8;
    for (const node of nodes) {
        node.x = snap(node.x - left + padding);
        node.y = snap(node.y - top + padding);
    }
    const width = Math.max(...nodes.map(node => node.x + node.width)) + padding;
    const graphHeight = Math.max(...nodes.map(node => node.y + node.height)) + padding;
    const centerX = selected.x + selected.width / 2, centerY = selected.y + selected.height / 2;
    const ordered = connections.map((connection, i) => {
        const other = byName.get(connection.node.name);
        return { ...connection, other, id: String(i),
            angle: Math.atan2(other.y + other.height / 2 - centerY, other.x + other.width / 2 - centerX) };
    }).sort((a, b) => a.angle - b.angle);
    const rootPorts = assignPorts(selected, ordered, edge => edge.other, mode);
    const neighborPorts = new Map();
    for (const node of nodes.slice(1)) {
        const edges = ordered.filter(edge => edge.other === node);
        const assigned = assignPorts(node, edges, () => selected, mode);
        for (const edge of edges) neighborPorts.set(edge.id, assigned.get(edge.id));
    }
    const routes = ordered.map(edge => ({ ...edge, rootPort: rootPorts.get(edge.id), otherPort: neighborPorts.get(edge.id) }));
    const router = mode === 'Topology' ? new Router(width, graphHeight, nodes, routes.flatMap(edge => [edge.rootPort, edge.otherPort])) : null;
    for (const edge of routes) {
        router?.reserve(edge.rootPort, edge.id);
        router?.reserve(edge.otherPort, edge.id);
    }
    const edges = routes.map(edge => {
        const source = edge.incoming ? edge.other : selected;
        const target = edge.incoming ? selected : edge.other;
        const from = edge.incoming ? edge.otherPort : edge.rootPort;
        const to = edge.incoming ? edge.rootPort : edge.otherPort;
        // Ranked layouts have obstacle-free gutters. Ordered ports admit monotone curves without shared rails.
        const points = mode !== 'Topology'
            ? [[from.x, from.y], [to.x + to.nx * 8, to.y + to.ny * 8]]
            : router.route(from, to, edge.id);
        return { id: edge.id, source: source.node.name, target: target.node.name,
            type: edge.node.type, path: d3.line()(points), points };
    });
    const allX = [...nodes.flatMap(node => [node.x, node.x + node.width]), ...edges.flatMap(edge => edge.points.map(point => point[0]))];
    const allY = [...nodes.flatMap(node => [node.y, node.y + node.height]), ...edges.flatMap(edge => edge.points.map(point => point[1]))];
    const cropX = Math.min(...allX) - 16, cropY = Math.min(...allY) - 16;
    for (const node of nodes) { node.x -= cropX; node.y -= cropY; }
    for (const edge of edges) {
        edge.points = edge.points.map(point => [point[0] - cropX, point[1] - cropY]);
        if (mode === 'Topology') {
            edge.path = roundedPath(edge.points);
        } else {
            const [x, y] = edge.points.at(-1);
            const target = byName.get(edge.target);
            const horizontal = mode === 'Flow';
            const direction = horizontal ? (x < target.x ? -1 : 1) : (y < target.y ? -1 : 1);
            const join = horizontal ? [x + direction * 16, y] : [x, y + direction * 16];
            // End the curve before the marker's solid neck. A sharp turn inside an arrowhead
            // visually separates its horizontal/vertical head from the still-curving shaft.
            edge.path = (horizontal ? d3.linkHorizontal() : d3.linkVertical())({
                source: edge.points[0], target: join
            }) + `L${x},${y}`;
        }
    }
    return { width: Math.max(...allX) - cropX + 16, height: Math.max(...allY) - cropY + 16, nodes, edges };
}

function roundedPath(points) {
    const path = d3.path();
    path.moveTo(...points[0]);
    for (let i = 1; i < points.length - 1; i++) {
        const previous = points[i - 1], point = points[i], next = points[i + 1];
        const before = Math.hypot(point[0] - previous[0], point[1] - previous[1]);
        const after = Math.hypot(next[0] - point[0], next[1] - point[1]);
        const radius = Math.min(8, before / 2, after / 2);
        path.lineTo(point[0] + (previous[0] - point[0]) * radius / before, point[1] + (previous[1] - point[1]) * radius / before);
        path.quadraticCurveTo(...point, point[0] + (next[0] - point[0]) * radius / after, point[1] + (next[1] - point[1]) * radius / after);
    }
    path.lineTo(...points.at(-1));
    return path.toString();
}

function groupPorts(node, edges, targetFor, mode) {
    const faces = new Map();
    for (const edge of edges) {
        const target = targetFor(edge);
        const dx = target.x + target.width / 2 - node.x - node.width / 2;
        const dy = target.y + target.height / 2 - node.y - node.height / 2;
        const horizontal = mode === 'Flow' || (mode === 'Topology' && Math.abs(dx) >= Math.abs(dy));
        const face = horizontal ? (dx < 0 ? 'left' : 'right') : (dy < 0 ? 'top' : 'bottom');
        if (!faces.has(face)) faces.set(face, []);
        const isTarget = node.isCenter === edge.incoming;
        faces.get(face).push({ edge, target, isTarget });
    }
    for (const [face, group] of faces) {
        const horizontal = face === 'left' || face === 'right';
        group.sort((a, b) => horizontal
            ? a.target.y - b.target.y || Number(a.edge.id) - Number(b.edge.id)
            : a.target.x - b.target.x || a.target.y - b.target.y || Number(a.edge.id) - Number(b.edge.id));
        const targetGaps = group.slice(1).filter((port, i) => port.isTarget || group[i].isTarget).length;
        const sourceGaps = group.length - 1 - targetGaps;
        const length = horizontal ? node.height : node.width;
        // Incoming arrowheads retain 16px separation. Outgoing strokes can use 2px slots
        // without growing a short-label box merely to reserve arrow-sized source ports.
        const sourceSpacing = Math.max(mode === 'Topology' ? 8 : 2, Math.min(8,
            Math.floor((length - 32 - targetGaps * 16) / Math.max(1, sourceGaps) / 2) * 2));
        group[0].offset = 0;
        for (let i = 1; i < group.length; i++) {
            // Arrow targets need more separation than outgoing strokes, including reciprocal typed pairs.
            group[i].offset = group[i - 1].offset + (group[i].isTarget || group[i - 1].isTarget ? 16 : sourceSpacing);
        }
    }
    return faces;
}

function assignPorts(node, edges, targetFor, mode) {
    const assigned = new Map();
    for (const [face, group] of groupPorts(node, edges, targetFor, mode)) {
        const horizontal = face === 'left' || face === 'right';
        const length = horizontal ? node.height : node.width;
        if (group.at(-1).offset + 32 > length) throw new Error(`Not enough separate ports on ${node.node.name}.`);
        const start = snap((length - group.at(-1).offset) / 2);
        group.forEach(({ edge, offset, isTarget }) => assigned.set(edge.id, {
            x: node.x + (horizontal ? (face === 'left' ? 0 : node.width) : start + offset),
            y: node.y + (horizontal ? start + offset : (face === 'top' ? 0 : node.height)),
            nx: face === 'left' ? -1 : face === 'right' ? 1 : 0,
            ny: face === 'top' ? -1 : face === 'bottom' ? 1 : 0,
            isTarget
        }));
    }
    return assigned;
}

// Topology uses measured node obstacles, ordered unique ports and no reused segments.
// A crossing is rendered with a background casing, not a joined trunk. Positive step costs prevent retracing.
class Router {
    constructor(width, height, nodes, ports) {
        // Coordinate compression keeps empty canvas out of A*. Include obstacle clearance and
        // lanes between adjacent ports so an earlier route cannot consume a later port's only exit.
        const xs = new Set([0, width]), ys = new Set([0, height]);
        for (const node of nodes) {
            for (const x of [node.x - 4, node.x, node.x + node.width, node.x + node.width + 4]) xs.add(x);
            for (const y of [node.y - 4, node.y, node.y + node.height, node.y + node.height + 4]) ys.add(y);
        }
        for (const port of ports) {
            xs.add(port.x); ys.add(port.y);
            for (const distance of [8, 16, 20, 32]) {
                xs.add(port.x + port.nx * distance);
                ys.add(port.y + port.ny * distance);
            }
            for (const offset of [-2, 2]) {
                xs.add(port.x + port.ny * offset);
                ys.add(port.y + port.nx * offset);
            }
        }
        const lanes = values => {
            const ordered = [...values].sort((a, b) => a - b);
            // Preserve a crossing lane between consecutive obstacle/port coordinates.
            return ordered.flatMap((value, i) => i ? [(ordered[i - 1] + value) / 2, value] : [value]);
        };
        this.xs = lanes(xs);
        this.ys = lanes(ys);
        this.xIndex = new Map(this.xs.map((x, i) => [x, i]));
        this.yIndex = new Map(this.ys.map((y, i) => [y, i]));
        this.columns = this.xs.length;
        this.rows = this.ys.length;
        this.count = this.columns * this.rows;
        this.blocked = new Uint8Array(this.count);
        this.used = new Map();
        this.terminals = new Map();
        this.vertices = new Uint8Array(this.count);
        // Reuse search storage rather than allocating and clearing a full grid for every relationship.
        this.distance = new Float64Array(this.count * 3);
        this.previous = new Int32Array(this.count * 3);
        this.visited = new Uint32Array(this.count * 3);
        this.settled = new Uint32Array(this.count * 3);
        this.search = 0;
        for (const node of nodes) {
            for (let y = this.yIndex.get(node.y); y <= this.yIndex.get(node.y + node.height); y++) {
                for (let x = this.xIndex.get(node.x); x <= this.xIndex.get(node.x + node.width); x++) {
                    this.blocked[y * this.columns + x] = 1;
                }
            }
        }
    }
    index(x, y) { return this.yIndex.get(y) * this.columns + this.xIndex.get(x); }
    key(a, b) { return Math.min(a, b) * this.count + Math.max(a, b); }
    reserve(port, owner) {
        const a = this.index(port.x, port.y);
        // Only arrow targets need the longer straight neck. Expanding both ends past
        // half the 48px obstacle clearance makes opposing terminals retrace each other.
        const escape = port.isTarget ? 32 : 16;
        const b = this.index(port.x + port.nx * escape, port.y + port.ny * escape);
        const step = port.nx + port.ny * this.columns;
        for (let at = a; at !== b; at += step) this.used.set(this.key(at, at + step), owner);
        this.terminals.set(a, owner);
        this.terminals.set(b, owner);
    }
    route(from, to, owner) {
        const start = this.index(from.x + from.nx * 16, from.y + from.ny * 16);
        const end = this.index(to.x + to.nx * 32, to.y + to.ny * 32);
        const { distance, previous, visited, settled } = this;
        const search = ++this.search;
        const heuristic = index => (Math.abs(this.xs[index % this.columns] - this.xs[end % this.columns]) +
            Math.abs(this.ys[Math.floor(index / this.columns)] - this.ys[Math.floor(end / this.columns)])) / grid;
        const queue = new MinHeap();
        distance[start * 3] = 0;
        previous[start * 3] = -1;
        visited[start * 3] = search;
        queue.push(start * 3, heuristic(start));
        let finish = -1;
        while (queue.items.length) {
            const state = queue.pop(), at = Math.floor(state / 3), axis = state % 3;
            if (settled[state] === search) continue;
            settled[state] = search;
            if (at === end) { finish = state; break; }
            for (const [offset, nextAxis] of [[-1, 1], [1, 1], [-this.columns, 2], [this.columns, 2]]) {
                const next = at + offset;
                if (next < 0 || next >= this.count || this.blocked[next]) continue;
                const terminalOwner = this.terminals.get(next);
                if (terminalOwner !== undefined && terminalOwner !== owner) continue;
                if (nextAxis === 1 && Math.floor(at / this.columns) !== Math.floor(next / this.columns)) continue;
                const usedBy = this.used.get(this.key(at, next));
                // Escape corridors are already included at the path's ends; revisiting even our own would retrace them.
                if (usedBy !== undefined) continue;
                const crossing = this.vertices[next] && !(this.vertices[next] & nextAxis) ? 10 : 0;
                const step = nextAxis === 1 ? Math.abs(this.xs[next % this.columns] - this.xs[at % this.columns])
                    : Math.abs(this.ys[Math.floor(next / this.columns)] - this.ys[Math.floor(at / this.columns)]);
                const cost = distance[state] + step / grid + (axis && axis !== nextAxis ? 5 : 0) + crossing;
                const nextState = next * 3 + nextAxis;
                if (visited[nextState] === search && cost >= distance[nextState]) continue;
                visited[nextState] = search;
                distance[nextState] = cost;
                previous[nextState] = state;
                queue.push(nextState, cost + heuristic(next));
            }
        }
        if (finish < 0) throw new Error(`No separate relationship route for edge ${owner}.`);
        const indices = [];
        for (let state = finish; state >= 0; state = previous[state]) indices.unshift(Math.floor(state / 3));
        for (let i = 1; i < indices.length; i++) {
            this.used.set(this.key(indices[i - 1], indices[i]), owner);
            const axis = Math.abs(indices[i] - indices[i - 1]) === 1 ? 1 : 2;
            this.vertices[indices[i]] |= axis;
            this.vertices[indices[i - 1]] |= axis;
        }
        const points = [
            [from.x, from.y],
            ...indices.map(index => [this.xs[index % this.columns], this.ys[Math.floor(index / this.columns)]]),
            [to.x + to.nx * 8, to.y + to.ny * 8]
        ];
        return points.filter((point, i) => i === 0 || i === points.length - 1 ||
            (points[i - 1][0] !== points[i + 1][0] && points[i - 1][1] !== points[i + 1][1]));
    }
}

class MinHeap {
    items = [];
    push(state, score) {
        const item = { state, score };
        let i = this.items.length;
        this.items.push(item);
        while (i > 0) {
            const parent = (i - 1) >> 1;
            if (this.items[parent].score <= score) break;
            this.items[i] = this.items[parent];
            i = parent;
        }
        this.items[i] = item;
    }
    pop() {
        const first = this.items[0], last = this.items.pop();
        if (this.items.length) {
            let i = 0;
            while (i * 2 + 1 < this.items.length) {
                let child = i * 2 + 1;
                if (child + 1 < this.items.length && this.items[child + 1].score < this.items[child].score) child++;
                if (last.score <= this.items[child].score) break;
                this.items[i] = this.items[child];
                i = child;
            }
            this.items[i] = last;
        }
        return first.state;
    }
}

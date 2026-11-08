// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph.js", import.meta.url), "utf8");
const d3Source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/d3.v7.min.js", import.meta.url), "utf8");
const geometry = runInNewContext(d3Source + "\nd3;", { setTimeout, clearTimeout, setInterval, clearInterval });

class Selection {
    attributes = new Map();
    handlers = new Map();
    classes = new Map();

    // element is what node() returns. environment, when set, is the fake layout of the graph's SVG (see createGraph).
    constructor(element = null, environment = null) {
        this.element = element;
        this.environment = environment;
    }

    // Elements appended to the graph's SVG (the base group the graph is drawn in) measure as the graph's content bounds.
    append() {
        return this.environment
            ? new Selection({ getBBox: () => this.environment.bounds }, this.environment)
            : new Selection();
    }
    selectAll() { return new Selection(); }
    select() { return this; }
    data(values) { this.values = values; return this; }
    join(enter) { if (typeof enter === "function") enter(this); return this; }
    style() { return this; }
    text() { return this; }
    attr(name, value) { this.attributes.set(name, value); return this; }
    call(fn, ...args) { this.calledBehavior = fn; this.environment?.calls.push({ fn, args }); return this; }
    on(name, handler) { this.handlers.set(name, handler); return this; }
    node() { return this.element; }
    transition() { return this; }
    enter() { return new Selection(); }
    exit() { return new Selection(); }
    remove() { return this; }
    merge(selection) { return selection; }
    classed(name, value) { this.classes.set(name, value); return this; }
    each() { return this; }
}

// Mirrors d3.zoomIdentity's chaining: translate(x, y) moves by (x, y) in the current scale, and scale(k) multiplies it.
// See https://d3js.org/d3-zoom#zoomTransform.
function zoomTransform(x, y, k) {
    return {
        x, y, k,
        translate(tx, ty) { return zoomTransform(x + (k * tx), y + (k * ty), k); },
        scale(s) { return zoomTransform(x, y, k * s); },
    };
}

// environment, when passed, fakes the graph's layout: { viewport: { clientWidth, clientHeight }, bounds: getBBox() result,
// calls: [] }. viewport is the SVG's parent element, the graph's drawing area. calls records selection.call(fn, ...args)
// on the SVG, and environment.zoomTransform is set to the zoom behavior's transform function to find zoom changes.
function createGraph(environment = null, enableTagSelection = true) {
    const behavior = {
        handlers: new Map(),
        forces: new Map(),
        id() { return this; },
        strength() { return this; },
        distance() { return this; },
        iterations() { return this; },
        scaleExtent() { return this; },
        on(name, handler) { this.handlers.set(name, handler); return this; },
        force(name, value) {
            if (arguments.length === 1) {
                return this.forces.get(name);
            }
            if (value === null) {
                this.forces.delete(name);
            } else {
                this.forces.set(name, value);
            }
            return this;
        },
        nodes(nodes) { this.simulationNodes = nodes; return this; },
        links() { return this; },
        stop() { return this; },
        alpha() { return this; },
        tick() { return this; },
        alphaTarget(value) { this.target = value; return this; },
        restart() { return this; },
        transform() { },
    };
    if (environment) {
        environment.zoomTransform = behavior.transform;
    }
    // The browser module imports D3 for its global side effect, imports the graph game and exports interop entry points.
    // Supply only the D3 construction API here; the actual selection/highlight and sizing logic runs unchanged.
    const ResourceGraph = runInNewContext(
        source.replace(/^import .*\r?$/gm, "").replace(/^export /gm, "") + "\nResourceGraph;",
        {
            document: { fonts: { status: "loaded" } },
            d3: {
                curveCatmullRomClosed: geometry.curveCatmullRomClosed,
                extent: geometry.extent,
                line: geometry.line,
                mean: geometry.mean,
                polygonHull: geometry.polygonHull,
                select: selector => environment && selector === ".resource-graph"
                    ? new Selection({ parentElement: environment.viewport }, environment)
                    : new Selection(),
                zoomIdentity: zoomTransform(0, 0, 1),
                zoom: () => behavior,
                drag: () => ({
                    handlers: new Map(),
                    on: behavior.on,
                    container() { return this; },
                    filter(accessor) { this.filterAccessor = accessor; return this; },
                    subject(accessor) { this.subjectAccessor = accessor; return this; },
                }),
                forceLink: () => behavior,
                forceSimulation: () => behavior,
                forceManyBody: () => behavior,
                forceCollide: () => behavior,
                forceX: geometry.forceX,
                forceY: geometry.forceY,
                forceCenter: () => behavior,
            },
        });
    return new ResourceGraph({ invokeMethodAsync: async () => {} }, null, enableTagSelection);
}

test("selection updates are safe before resources arrive and highlight them afterward", () => {
    const graph = createGraph();

    graph.switchTo("api");
    assert.equal(graph.selectedNode, undefined);
    assert.equal(graph.nodeElements.attributes.get("class")({ id: "api" }), "resource-group");
    assert.equal(graph.linkElements.attributes.get("class")({ source: { id: "api" }, target: { id: "cache" } }), "resource-link");

    graph.switchTo(null);
    graph.contextMenuChanged(false);
    assert.equal(graph.openContextMenu, false);

    graph.nodes = [{ id: "api" }, { id: "cache" }, { id: "worker" }];
    graph.links = [{ source: graph.nodes[0], target: graph.nodes[1] }];

    graph.switchTo("api");
    const nodeClass = graph.nodeElements.attributes.get("class");
    assert.equal(nodeClass(graph.nodes[0]), "resource-group resource-group-selected resource-group-highlight");
    assert.equal(nodeClass(graph.nodes[1]), "resource-group resource-group-highlight");
    assert.equal(nodeClass(graph.nodes[2]), "resource-group");
    assert.equal(graph.linkElements.attributes.get("class")(graph.links[0]), "resource-link-highlight");

    graph.switchTo(null);
    assert.equal(graph.nodeElements.attributes.get("class")(graph.nodes[0]), "resource-group");
    assert.equal(graph.linkElements.attributes.get("class")(graph.links[0]), "resource-link");
});

// The zoom transforms applied to the graph since the environment's calls were last cleared, as plain { x, y, k } values.
function appliedZoomTransforms(environment) {
    return environment.calls
        .filter(call => call.fn === environment.zoomTransform)
        .map(call => ({ x: call.args[0].x, y: call.args[0].y, k: call.args[0].k }));
}

test("the initial fit waits until the graph has a size and later resizes keep the user's zoom", () => {
    const environment = { viewport: { clientWidth: 0, clientHeight: 0 }, bounds: { x: 0, y: 0, width: 200, height: 100 }, calls: [] };
    const graph = createGraph(environment);

    // Resources arrive while the graph is hidden behind another tab. There's nothing to measure, so it isn't fitted yet.
    graph.nodes = [{ id: "api" }];
    assert.equal(graph.fitGraph(false), false);
    graph.resize();
    assert.equal(graph.hasFitGraph, false);
    assert.equal(graph.svg.attributes.has("viewBox"), false);

    // The graph is shown. Its first resize sizes the viewBox from the drawing area and fits the graph: the 200x100
    // content fits at the maximum scale of 1, centered on its middle (100, 50).
    environment.calls.length = 0;
    environment.viewport.clientWidth = 800;
    environment.viewport.clientHeight = 600;
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-400, -300, 800, 600]);
    assert.equal(graph.hasFitGraph, true);
    assert.deepEqual(appliedZoomTransforms(environment), [{ x: -100, y: -50, k: 1 }]);

    // Later resizes only follow the drawing area, so they keep whatever zoom and pan the user has applied since.
    environment.calls.length = 0;
    environment.viewport.clientWidth = 1000;
    environment.viewport.clientHeight = 700;
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-500, -350, 1000, 700]);
    assert.deepEqual(appliedZoomTransforms(environment), []);
});

test("a sized graph waits for resources before its initial fit and keeps its viewBox while hidden", () => {
    const environment = { viewport: { clientWidth: 800, clientHeight: 600 }, bounds: { x: 0, y: 0, width: 200, height: 100 }, calls: [] };
    const graph = createGraph(environment);

    // No resources yet: the viewBox follows the drawing area, and the initial fit waits for resources.
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-400, -300, 800, 600]);
    assert.equal(graph.hasFitGraph, false);
    assert.deepEqual(appliedZoomTransforms(environment), []);

    // Hiding the graph (another tab or overview mode) leaves it without a size. Keep the last viewBox for when it's shown.
    environment.viewport.clientWidth = 0;
    environment.viewport.clientHeight = 0;
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-400, -300, 800, 600]);
});

function createTags(...names) {
    return names.map(name => ({ name, color: "var(--accent-teal)" }));
}

test("tag labels select their group with clicks and preserve selection modifiers without a keyboard tab stop", async () => {
    const graph = createGraph();
    graph.nodes = [{ id: "api", tags: createTags("Backend") }];
    graph.updateTagGroups();
    const labels = graph.tagControls.select("text");
    const calls = [];
    graph.resourcesInterop.invokeMethodAsync = async (...args) => calls.push(args);
    let prevented = 0;
    let stopped = 0;
    const event = {
        currentTarget: { __data__: graph.tagGroups[0] },
        ctrlKey: true,
        metaKey: false,
        shiftKey: true,
        preventDefault: () => prevented++,
        stopPropagation: () => stopped++,
    };

    assert.equal(labels.attributes.has("tabindex"), false);
    assert.equal(labels.handlers.has("keydown"), false);
    assert.equal(labels.attributes.get("pointer-events"), "all");
    await labels.handlers.get("click")(event);
    assert.deepEqual(calls.map(([method, tag, modifiers]) => [method, tag, { ...modifiers }]), [
        ["SelectTagGroup", "Backend", { ctrlKey: true, metaKey: false, shiftKey: true }]
    ]);
    assert.equal(prevented, 1);
    assert.equal(stopped, 1);
    labels.handlers.get("mousedown touchstart")(event);
    assert.equal(stopped, 2);
});

test("hovering the tag controls highlights the tag area and its members together", () => {
    const graph = createGraph();
    const shared = { id: "shared", tags: createTags("Backend", "API") };
    const backend = { id: "backend", tags: createTags("backend"), isHidden: true };
    const otherApi = { id: "other-api", tags: createTags("API") };
    const neighbor = { id: "neighbor", tags: [] };
    const selected = { id: "selected", tags: [] };
    const unrelated = { id: "unrelated", tags: [] };
    graph.nodes = [shared, backend, otherApi, neighbor, selected, unrelated];
    graph.links = [
        { source: shared, target: neighbor },
        { source: backend, target: shared },
        { source: neighbor, target: selected },
        { source: otherApi, target: unrelated },
    ];
    graph.updateTagGroups();
    graph.switchTo(selected.id);
    const nodeClasses = () => graph.nodes.map(graph.nodeElements.attributes.get("class"));
    const linkClasses = () => graph.links.map(graph.linkElements.attributes.get("class"));
    const originalNodeClasses = nodeClasses();
    const originalLinkClasses = linkClasses();
    const tagFillHighlights = selection => graph.tagGroups.map(selection.classes.get("resource-tag-fill-hover"));
    const backendGroup = graph.tagGroups.find(group => group.key === "BACKEND");

    graph.tagControls.handlers.get("mouseenter")({}, backendGroup);

    for (const paths of [graph.tagElements, graph.nodeTagElements]) {
        assert.deepEqual(tagFillHighlights(paths), graph.tagGroups.map(group => group === backendGroup));
    }
    assert.deepEqual(nodeClasses(), [
        "resource-group resource-group-hover resource-group-highlight",
        "resource-group resource-group-hidden resource-group-hover resource-group-highlight",
        "resource-group",
        "resource-group resource-group-highlight",
        "resource-group resource-group-selected resource-group-highlight",
        "resource-group",
    ]);
    assert.deepEqual(linkClasses(), [
        "resource-link-highlight",
        "resource-link-highlight",
        "resource-link-highlight-expand",
        "resource-link",
    ]);
    assert.equal(graph.selectedNode, selected);

    graph.tagControls.handlers.get("mouseleave")();
    for (const paths of [graph.tagElements, graph.nodeTagElements]) {
        assert.ok(tagFillHighlights(paths).every(highlighted => !highlighted));
    }
    assert.deepEqual(nodeClasses(), originalNodeClasses);
    assert.deepEqual(linkClasses(), originalLinkClasses);

    graph.hoverNode({ currentTarget: { __data__: backend } });
    assert.equal(nodeClasses()[0], "resource-group resource-group-highlight");
    assert.equal(nodeClasses()[1], "resource-group resource-group-hidden resource-group-hover resource-group-highlight");
    graph.unHoverNode();
    assert.deepEqual(nodeClasses(), originalNodeClasses);
    assert.deepEqual(linkClasses(), originalLinkClasses);
});

test("graphs without pane selection can drag tag labels but cannot select them", () => {
    const graph = createGraph(null, false);
    graph.nodes = [{ id: "api", tags: createTags("Backend") }];
    graph.updateTagGroups();
    const labels = graph.tagControls.select("text");
    assert.equal(labels.attributes.get("role"), undefined);
    assert.equal(labels.attributes.get("tabindex"), undefined);
    assert.equal(labels.handlers.has("click"), false);
    assert.equal(labels.handlers.has("keydown"), false);
    assert.equal(labels.calledBehavior, graph.tagGroupDrag);
});

test("tag groups use supplied selector colors regardless of encounter order and refresh colors on updates", () => {
    const graph = createGraph();
    const backend = { name: "Backend", color: "var(--accent-brass)" };
    const frontend = { name: "Frontend", color: "var(--accent-coral)" };
    const node = { id: "shared", tags: [frontend, backend] };
    graph.nodes = [node];
    graph.updateTagGroups();
    assert.equal(graph.tagGroups.find(group => group.key === "BACKEND").color, backend.color);
    assert.equal(graph.tagGroups.find(group => group.key === "FRONTEND").color, frontend.color);

    node.tags = [backend, frontend];
    graph.updateTagGroups();
    assert.equal(graph.tagGroups.find(group => group.key === "BACKEND").color, backend.color);
    assert.equal(graph.tagGroups.find(group => group.key === "FRONTEND").color, frontend.color);

    node.tags = [{ name: "backend", color: "var(--accent-orchid)" }];
    graph.updateTagGroups();
    assert.equal(graph.tagGroups[0].color, node.tags[0].color);
    assert.equal(graph.nodeTagElements.values[0].color, node.tags[0].color);
});

test("overlapping tags share nodes, coextensive tags share anchors, and tag removal removes boundaries", () => {
    const graph = createGraph();
    const shared = { id: "shared", tags: createTags("Backend", "API", "api"), x: 0, y: 0 };
    const backend = { id: "backend", tags: createTags("backend"), x: 100, y: 100 };
    const frontend = { id: "frontend", tags: createTags("Frontend", "Storefront"), x: 200, y: 200 };
    const untagged = { id: "untagged", tags: [], x: 300, y: 300 };
    graph.nodes = [shared, backend, frontend, untagged];
    graph.updateTagGroups();

    const groups = new Map(graph.tagGroups.map(group => [group.key, group]));
    assert.equal(groups.size, 4);
    assert.deepEqual([...groups.get("BACKEND").nodes], [shared, backend]);
    assert.deepEqual([...groups.get("API").nodes], [shared]);
    assert.equal(shared.tagX, (groups.get("BACKEND").anchor.x + groups.get("API").anchor.x) / 2);
    assert.equal(shared.tagY, (groups.get("BACKEND").anchor.y + groups.get("API").anchor.y) / 2);
    assert.equal(groups.get("FRONTEND").anchor, groups.get("STOREFRONT").anchor);
    assert.notEqual(groups.get("FRONTEND").level, groups.get("STOREFRONT").level);
    assert.ok(groups.get("BACKEND").level > groups.get("API").level);
    assert.equal(untagged.tagX, 0);
    assert.equal(untagged.tagY, 0);

    const color = groups.get("BACKEND").color;
    shared.tags = createTags("Backend");
    frontend.tags = [];
    graph.updateTagGroups();
    assert.deepEqual([...graph.tagGroups.map(group => group.key)], ["BACKEND"]);
    assert.equal(graph.tagGroups[0].color, color);
    graph.nodes = [];
    graph.updateTagGroups();
    assert.equal(graph.tagGroups.length, 0);
});

test("node backgrounds replay all group fills in paint order and remove fills when tags change", () => {
    const graph = createGraph();
    const shared = { id: "shared", tags: createTags("API", "Backend", "backend", "Frontend") };
    const single = { id: "single", tags: createTags("Frontend") };
    const unrelated = { id: "unrelated", tags: createTags("Other") };
    graph.nodes = [shared, single, unrelated];
    graph.updateTagGroups();

    assert.deepEqual([...graph.nodeTagElements.values.map(group => group.key)], ["FRONTEND", "OTHER", "BACKEND", "API"]);
    assert.deepEqual([...graph.nodeTagElements.values], [...graph.tagElements.values]);

    shared.tags = createTags("Backend", "API");
    single.tags = [];
    unrelated.tags = [];
    graph.updateTagGroups();
    assert.deepEqual([...graph.nodeTagElements.values.map(group => group.key)], ["BACKEND", "API"]);
    assert.deepEqual([...graph.nodeTagElements.values], [...graph.tagElements.values]);

    shared.tags = [];
    graph.updateTagGroups();
    assert.equal(graph.nodeTagElements.values.length, 0);
});

test("overlapping groups get nearby anchors rather than being separated by unrelated tags", () => {
    const graph = createGraph();
    graph.nodes = [
        { id: "shared", tags: createTags("A", "C") },
        { id: "a", tags: createTags("A") },
        { id: "b", tags: createTags("B") },
        { id: "d", tags: createTags("D") },
        { id: "z", tags: createTags("Z") }
    ];
    graph.updateTagGroups();
    const anchors = new Map(graph.tagGroups.map(group => [group.key, group.anchor]));
    const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
    assert.ok(distance(anchors.get("A"), anchors.get("C")) < distance(anchors.get("A"), anchors.get("D")));
});

test("adding nested tags does not push disjoint groups farther from the graph center", () => {
    const graph = createGraph();
    const hierarchy = Array.from({ length: 8 }, (_, i) => ({ id: `tree-${i}`, tags: createTags("Hierarchy") }));
    graph.nodes = [...hierarchy, { id: "worker", tags: createTags("Workers") }];
    const distanceFromCenter = key => {
        const { anchor } = graph.tagGroups.find(group => group.key === key);
        return Math.hypot(anchor.x, anchor.y);
    };

    graph.updateTagGroups();
    const initialDistance = distanceFromCenter("HIERARCHY");
    hierarchy[0].tags.push(...createTags("Roots"));
    hierarchy[1].tags.push(...createTags("Branches"));
    hierarchy[2].tags.push(...createTags("Leaves"));
    graph.updateTagGroups();

    assert.ok(distanceFromCenter("HIERARCHY") <= initialDistance + 1);
    assert.ok(distanceFromCenter("WORKERS") <= initialDistance + 1);
});

function createPhysicsNode(id, tags, x, y) {
    return { id, tags: createTags(...tags), x, y, vx: 0, vy: 0, bounds: { x: -56, y: -70, width: 112, height: 150 } };
}

test("tag groups stack only when their labels and action buttons substantially overlap", () => {
    const graph = createGraph();
    const shared = createPhysicsNode("shared", ["A", "B", "C"], 100, -200);
    const left = createPhysicsNode("left", ["A"], 0, 0);
    graph.nodes = [shared, left, createPhysicsNode("right", ["B"], 240, 0), createPhysicsNode("bottom", ["C"], 100, 240)];
    graph.updateTagGroups();

    for (const group of graph.tagGroups) {
        // A 40-unit text label followed by its action button.
        group.controlBounds = { x: 0, y: -19, width: 72, height: 28 };
    }

    const stacked = tagControlPositions(graph);
    const rows = [...stacked.values()].sort((a, b) => a.y - b.y);
    assert.ok(rows.every(row => row.x === rows[0].x));
    assert.ok(rows[1].y - rows[0].y >= 28);
    assert.ok(rows[2].y - rows[1].y >= 28);

    left.y = shared.y;
    left.x = shared.x - 30;
    const withButtonCollision = tagControlPositions(graph);
    assert.equal(withButtonCollision.get("B").x - withButtonCollision.get("A").x, 30);
    assert.ok(Math.abs(withButtonCollision.get("B").y - withButtonCollision.get("A").y) >= 28);

    left.x = shared.x - 55;
    assert.equal(tagControlPositions(graph).get("A").y, stacked.get("C").y);
});

test("tags on different boundaries stay with their hulls when the controls only graze vertically", () => {
    const graph = createGraph();
    graph.nodes = [createPhysicsNode("shell", ["Backend", "Development"], 100, -200)];
    graph.updateTagGroups();
    for (const group of graph.tagGroups) {
        group.controlBounds = { x: 0, y: -23, width: 110, height: 34 };
    }

    const positions = tagControlPositions(graph);
    for (const group of graph.tagGroups) {
        const [x, y] = group.labelAnchor;
        assert.deepEqual(positions.get(group.key), { x, y });
    }
});

function tagControlPositions(graph) {
    graph.tagElements.attr = function (name, value) {
        this.values.forEach(value);
        return this;
    };
    graph.onTick();
    const transform = graph.tagControls.attributes.get("transform");
    return new Map(graph.tagControls.values.map(group => {
        const [x, y] = transform(group).slice("translate(".length, -1).split(",").map(Number);
        return [group.key, { x, y }];
    }));
}

test("group cohesion pulls unlinked members together, cools and does not double count aliases", () => {
    const graph = createGraph();
    const a = createPhysicsNode("a", ["Backend"], -500, 0);
    const b = createPhysicsNode("b", ["Backend"], 500, 0);
    graph.nodes = [a, b];
    graph.updateTagGroups();
    graph.simulation.force("tagCohesion")(1);
    assert.ok(a.vx > 0 && b.vx < 0);
    assert.equal(a.vx, -b.vx);
    const fullStrength = a.vx;

    a.vx = b.vx = 0;
    graph.simulation.force("tagCohesion")(0.5);
    assert.equal(a.vx, fullStrength / 2);

    a.tags.push(...createTags("Alias"));
    b.tags.push(...createTags("Alias"));
    a.vx = b.vx = 0;
    graph.updateTagGroups();
    graph.simulation.force("tagCohesion")(1);
    assert.equal(a.vx, fullStrength);
    assert.equal(b.vx, -fullStrength);
    assert.deepEqual([a.x, a.y, b.x, b.y], [-500, 0, 500, 0]);
});

test("shared members are pulled between their groups' actual centers", () => {
    const graph = createGraph();
    const a = createPhysicsNode("a", ["A"], -400, 0);
    const shared = createPhysicsNode("shared", ["A", "B"], 0, 0);
    const b = createPhysicsNode("b", ["B"], 0, 400);
    graph.nodes = [a, shared, b];
    graph.updateTagGroups();
    graph.simulation.force("tagCohesion")(1);
    assert.ok(shared.vx < 0 && shared.vy > 0);
    assert.equal(shared.vx, -shared.vy);
    assert.equal(shared.vx, -a.vx / 2);
    assert.equal(shared.vy, -b.vy / 2);
});

test("group collisions separate disjoint tags without cooling away or moving shared groups apart", () => {
    for (const { tags, distance, repels } of [
        { tags: ["Frontend"], distance: 50, repels: true },
        { tags: ["backend", "api"], distance: 50, repels: false },
        { tags: ["backend", "Other"], distance: 50, repels: true },
        { tags: ["Frontend"], distance: 10000, repels: false }
    ]) {
        const graph = createGraph();
        const a = createPhysicsNode("a", ["Backend", "API"], 0, 0);
        const b = createPhysicsNode("b", tags, distance, 0);
        graph.nodes = [a, b];
        graph.updateTagGroups();
        const force = graph.simulation.force("tagSeparation");
        force(0);
        if (repels) {
            assert.ok(a.vx < 0 && b.vx > 0);
            assert.equal(a.vx, -b.vx);
        } else {
            assert.deepEqual([a.vx, a.vy, b.vx, b.vy], [0, 0, 0, 0]);
        }
        assert.deepEqual([a.x, a.y, b.x, b.y], [0, 0, distance, 0]);
    }
});

test("crossing group hulls repel even when every member pair is far apart", () => {
    const graph = createGraph();
    graph.nodes = [
        createPhysicsNode("left", ["A"], -600, 0),
        createPhysicsNode("right", ["A"], 600, 0),
        createPhysicsNode("top", ["B"], 0, -600),
        createPhysicsNode("bottom", ["B"], 0, 600)
    ];
    graph.updateTagGroups();
    graph.simulation.force("tagSeparation")(0);
    assert.ok(graph.nodes.some(node => Math.hypot(node.vx, node.vy) > 0));
    assert.ok(graph.nodes.every(node => Number.isFinite(node.vx) && Number.isFinite(node.vy)));
});

test("group collisions leave members away from the contacting edges undisturbed", () => {
    const graph = createGraph();
    const far = createPhysicsNode("far", ["A"], -600, 0);
    const near = createPhysicsNode("near", ["A"], 0, 0);
    const other = createPhysicsNode("other", ["B"], 100, 0);
    graph.nodes = [far, near, other];
    graph.updateTagGroups();
    graph.simulation.force("tagSeparation")(0);
    assert.deepEqual([far.vx, far.vy], [0, 0]);
    assert.ok(near.vx < 0 && other.vx > 0);
});

test("untagged exclusion follows elongated hulls and stops applying after tag changes", () => {
    const graph = createGraph();
    const left = createPhysicsNode("left", ["Backend", "Alias"], -600, 0);
    const right = createPhysicsNode("right", ["Backend", "Alias"], 600, 0);
    const near = createPhysicsNode("near", [], 450, 30);
    const far = createPhysicsNode("far", [], -2000, -2000);
    graph.nodes = [left, right, near, far];
    graph.updateTagGroups();
    graph.simulation.force("untaggedRepulsion")(0);
    assert.ok(near.vy > 0);
    assert.ok(left.vy < 0);
    assert.equal(left.vy, right.vy);
    assert.deepEqual([far.vx, far.vy], [0, 0]);

    for (const node of graph.nodes) {
        node.vx = node.vy = 0;
    }
    near.tags = createTags("Backend");
    graph.updateTagGroups();
    graph.simulation.force("untaggedRepulsion")(0);
    assert.ok(graph.nodes.every(node => node.vx === 0 && node.vy === 0));

    for (const node of graph.nodes) {
        node.tags = [];
    }
    graph.updateTagGroups();
    assert.equal(graph.simulation.force("tagSeparation"), undefined);
    assert.equal(graph.simulation.force("untaggedRepulsion"), undefined);
    assert.equal(graph.simulation.force("tagCohesion"), undefined);
});

test("group forces separate coincident resources without invalid velocities", () => {
    const graph = createGraph();
    const a = createPhysicsNode("a", ["A"], 0, 0);
    const b = createPhysicsNode("b", ["B"], 0, 0);
    const untagged = createPhysicsNode("untagged", [], 0, 0);
    graph.nodes = [a, b, untagged];
    graph.updateTagGroups();
    graph.simulation.force("tagSeparation")(0);
    assert.ok(Math.hypot(a.vx, a.vy) > 0);
    assert.ok(a.vx === -b.vx && a.vy === -b.vy);
    assert.equal(untagged.vx, 0);
    graph.simulation.force("untaggedRepulsion")(0);
    assert.ok(Math.hypot(untagged.vx, untagged.vy) > 0);
    assert.ok(graph.nodes.every(node => Number.isFinite(node.vx) && Number.isFinite(node.vy)));
});

test("untagged exclusion clears the complete node footprint from a pinned group's hull", () => {
    const graph = createGraph();
    const member = createPhysicsNode("member", ["Backend"], 0, 0);
    member.fx = member.fy = 0;
    const untagged = createPhysicsNode("untagged", [], 20, 0);
    graph.nodes = [member, untagged];
    graph.updateTagGroups();
    const simulation = geometry.forceSimulation(graph.nodes).stop()
        .force("untaggedRepulsion", graph.simulation.force("untaggedRepulsion"));
    simulation.tick(120);
    const hull = graph.getTagHull(graph.tagGroups[0]);
    for (const point of graph.getPredictedNodeHull(untagged)) {
        assert.equal(geometry.polygonContains(hull, point), false);
    }
    assert.deepEqual([member.x, member.y, member.fx, member.fy], [0, 0, 0, 0]);
    assert.ok(Number.isFinite(untagged.x) && Number.isFinite(untagged.y));
});

test("isolated resources settle closer to a tagged graph without entering its boundary", () => {
    const graph = createGraph();
    const icon = { path: "", color: "", tooltip: "" };
    const api = { name: "api", displayName: "api", tags: createTags("Backend"), referencedNames: ["worker"], resourceIcon: icon, stateIcon: icon };
    const worker = { ...api, name: "worker", tags: [], referencedNames: [] };
    const isolated = { ...worker, name: "isolated", referencedNames: ["filtered-out"] };
    graph.updateNodes([api, worker, isolated]);

    const [apiNode, workerNode, isolatedNode] = graph.nodes;
    const bounds = { x: -56, y: -70, width: 112, height: 150 };
    Object.assign(apiNode, { bounds, fx: 0, fy: 0 });
    Object.assign(workerNode, { bounds, fx: -200, fy: 0 });
    Object.assign(isolatedNode, { bounds, x: 800, y: 0 });
    graph.updateTagGroups();
    const simulation = geometry.forceSimulation(graph.nodes).stop()
        .force("charge", geometry.forceManyBody().strength(-800))
        .force("collide", geometry.forceCollide(node => graph.getCollisionRadius(node)).iterations(10))
        .force("x", graph.simulation.force("x"))
        .force("y", graph.simulation.force("y"))
        .force("untaggedRepulsion", graph.simulation.force("untaggedRepulsion"));
    simulation.tick(300);

    const distance = Math.hypot(isolatedNode.x - apiNode.x, isolatedNode.y - apiNode.y);
    assert.ok(distance < 310, `Unexpected isolated resource distance: ${distance}`);
    const hull = graph.getTagHull(graph.tagGroups[0]);
    for (const point of graph.getPredictedNodeHull(isolatedNode)) {
        assert.equal(geometry.polygonContains(hull, point), false);
    }
});

test("cohesion brings unlinked group members together without reducing collision spacing", () => {
    const graph = createGraph();
    const a = createPhysicsNode("a", ["Group"], -500, 0);
    const b = createPhysicsNode("b", ["Group"], 500, 0);
    graph.nodes = [a, b];
    graph.updateTagGroups();
    const simulation = geometry.forceSimulation(graph.nodes).stop()
        .force("collide", geometry.forceCollide(node => graph.getCollisionRadius(node)).iterations(10))
        .force("cohesion", graph.simulation.force("tagCohesion"));
    simulation.tick(300);
    const distance = Math.hypot(a.x - b.x, a.y - b.y);
    const spacing = graph.getCollisionRadius(a) + graph.getCollisionRadius(b);
    assert.ok(distance >= spacing - 1 && distance < spacing + 1, `Unexpected member spacing: ${distance}`);
});

test("tag hulls enclose measured resource labels for singleton and collinear groups and follow movement", () => {
    const graph = createGraph();
    const bounds = { x: -180, y: -70, width: 360, height: 165 };
    const first = { id: "first", tags: createTags("Group"), x: 10, y: 20, bounds };
    const second = { id: "second", tags: createTags("Group"), x: 500, y: 20, bounds };
    for (const nodes of [[first], [first, second]]) {
        graph.nodes = nodes;
        graph.updateTagGroups();
        const group = graph.tagGroups[0];
        const hull = graph.getTagHull(group);
        assert.ok(hull.length >= 4);
        for (const node of nodes) {
            for (const x of [bounds.x, bounds.x + bounds.width]) {
                for (const y of [bounds.y, bounds.y + bounds.height]) {
                    assert.equal(geometry.polygonContains(hull, [node.x + x, node.y + y]), true);
                }
            }
        }
        first.x += 1000;
        const moved = graph.getTagHull(group);
        assert.notDeepEqual(moved, hull);
        assert.equal(geometry.polygonContains(moved, [first.x, first.y]), true);
    }
});

test("tag-only updates change graph structure while preserving existing node positions", () => {
    const graph = createGraph();
    const original = {
        name: "api", displayName: "api", isHidden: false, referencedNames: [],
        resourceIcon: { path: "", color: "", tooltip: "" }, stateIcon: { path: "", color: "", tooltip: "" },
        tags: createTags("Backend")
    };
    const updated = { ...original, tags: createTags("Backend", "API") };
    const changed = resource => graph.resourcesChanged([original], new Map([[resource.name, resource]]));
    assert.equal(changed(original), false);
    assert.equal(changed({ ...original, tags: createTags("Backend") }), false);
    assert.equal(changed(updated), true);
    assert.equal(changed({ ...original, tags: [{ name: "Backend", color: "var(--accent-coral)" }] }), true);
    graph.nodes = [{ id: "api", x: 25, y: 50, vx: 1, vy: 2 }];
    graph.updateNodes([updated]);
    const node = graph.nodes[0];
    assert.deepEqual(node.tags, createTags("Backend", "API"));
    assert.deepEqual([node.x, node.y, node.vx, node.vy], [25, 50, 1, 2]);
});

test("status updates paint the latest state without relaying out a reordered resource list", () => {
    const graph = createGraph();
    const icon = { path: "", color: "", tooltip: "Running" };
    const api = {
        name: "api", displayName: "api", isHidden: false, referencedNames: ["cache"], tags: createTags("Backend"),
        resourceIcon: icon, stateIcon: icon, endpointText: "localhost:8080"
    };
    const cache = { ...api, name: "cache", displayName: "cache", referencedNames: [] };
    let restarts = 0;
    graph.simulation.restart = () => { restarts++; return graph.simulation; };
    graph.updateResources([api, cache]);
    assert.equal(restarts, 1);
    const [apiNode, cacheNode] = graph.nodes;
    Object.assign(apiNode, { x: 25, y: 50 });
    const links = graph.links;
    const backend = graph.tagGroups[0];
    const stopped = { ...api, stateIcon: { ...icon, tooltip: "Stopped" }, endpointText: "No endpoints" };

    graph.updateResources([cache, stopped]);
    assert.equal(restarts, 1);
    assert.equal(graph.nodes[0], apiNode);
    assert.equal(graph.nodes[1], cacheNode);
    assert.deepEqual([apiNode.x, apiNode.y], [25, 50]);
    assert.equal(apiNode.stateIcon.tooltip, "Stopped");
    assert.equal(apiNode.endpointText, "No endpoints");
    assert.equal(graph.links, links);
    assert.equal(graph.tagGroups[0], backend);

    graph.updateResources([cache, { ...api, referencedNames: [] }]);
    assert.equal(restarts, 2);
    assert.equal(graph.links.length, 0);
    assert.equal(apiNode.stateIcon.tooltip, "Running");
});

test("returning resources get the same layout and fit as a fresh graph", () => {
    const icon = { path: "", color: "", tooltip: "" };
    const api = {
        name: "api", displayName: "api", isHidden: false, referencedNames: ["cache"], tags: createTags("Backend"),
        resourceIcon: icon, stateIcon: icon
    };
    const cache = { ...api, name: "cache", displayName: "cache", referencedNames: [], tags: [] };
    const environment = {
        viewport: { clientWidth: 800, clientHeight: 600 },
        bounds: { x: -200, y: -150, width: 400, height: 300 },
        calls: []
    };
    const graph = createGraph(environment);
    const simulation = geometry.forceSimulation().stop();
    graph.simulation.nodes = nodes => {
        simulation.nodes(nodes);
        return graph.simulation;
    };
    const positions = () => Array.from(graph.nodes, node => [node.x, node.y, node.vx, node.vy]);

    graph.updateResources([api, cache]);
    const freshPositions = positions();
    const freshFit = appliedZoomTransforms(environment);
    const apiNode = graph.nodes[0];

    graph.updateResources([api]);
    Object.assign(apiNode, { x: 150, y: 200, vx: 4, vy: -2 });
    assert.deepEqual(appliedZoomTransforms(environment), freshFit);

    graph.updateResources([api, cache]);
    assert.equal(graph.nodes[0], apiNode);
    assert.deepEqual(positions(), freshPositions);
    assert.deepEqual(appliedZoomTransforms(environment), [...freshFit, ...freshFit]);
});

test("the graph preserves measured tag bounds while hidden and redraws them on resize", () => {
    const bounds = { x: -56, y: -56, width: 112, height: 132 };
    const viewport = { clientWidth: 800, clientHeight: 600 };
    const graph = createGraph({ viewport, bounds, calls: [] });
    let measuredBounds = bounds;
    const icon = { path: "", color: "", tooltip: "Running" };
    const api = {
        name: "api", displayName: "api", isHidden: false, referencedNames: [], tags: createTags("Backend"),
        resourceIcon: icon, stateIcon: icon
    };
    graph.nodeElementsG.selectAll = () => {
        const selection = new Selection();
        selection.each = function (callback) {
            for (const node of this.values ?? []) {
                callback.call({ getBBox: () => measuredBounds }, node);
            }
            return this;
        };
        return selection;
    };
    graph.updateResources([api]);
    const node = graph.nodes[0];
    Object.assign(node, { x: 25, y: 50 });
    const hull = graph.getTagHull(graph.tagGroups[0]);

    viewport.clientHeight = 0;
    measuredBounds = { x: 0, y: 0, width: 0, height: 0 };
    graph.updateResources([{ ...api, stateIcon: { ...icon, tooltip: "Stopped" } }]);
    graph.resize();
    assert.deepEqual(node.bounds, bounds);
    assert.equal(node.stateIcon.tooltip, "Stopped");
    assert.deepEqual(graph.getTagHull(graph.tagGroups[0]), hull);

    let redrawnHull;
    graph.onTick = () => { redrawnHull = graph.getTagHull(graph.tagGroups[0]); };
    measuredBounds = { ...bounds, width: 140 };
    viewport.clientHeight = 600;
    graph.resize();
    assert.deepEqual([node.x, node.y], [25, 50]);
    assert.deepEqual(node.bounds, measuredBounds);
    assert.deepEqual(redrawnHull, graph.getTagHull(graph.tagGroups[0]));
    assert.notDeepEqual(redrawnHull, hull);
});

test("resource updates during a drag keep its subject live and release its fixed position on mouseup", () => {
    const graph = createGraph();
    const resource = {
        name: "api", displayName: "api", isHidden: false, referencedNames: [], tags: createTags("Backend"),
        resourceIcon: { path: "", color: "", tooltip: "" }, stateIcon: { path: "", color: "", tooltip: "Starting" }
    };
    graph.updateNodes([resource]);
    const subject = graph.nodes[0];
    subject.x = 25;
    subject.y = 50;
    graph.dragDrop.handlers.get("start")({ subject, active: 0 });
    graph.dragDrop.handlers.get("drag")({ subject, x: 100, y: 200 });

    graph.updateNodes([{ ...resource, stateIcon: { ...resource.stateIcon, tooltip: "Running" } }]);
    assert.equal(graph.nodes[0], subject);
    assert.equal(subject.stateIcon.tooltip, "Running");
    graph.dragDrop.handlers.get("drag")({ subject, x: 300, y: 400 });
    assert.deepEqual([graph.nodes[0].fx, graph.nodes[0].fy], [300, 400]);

    graph.dragDrop.handlers.get("end")({ subject });
    assert.deepEqual([graph.nodes[0].fx, graph.nodes[0].fy], [null, null]);
});

test("dragging a tag label translates its members and hull together, even across a resource refresh", () => {
    const graph = createGraph();
    const resource = {
        isHidden: false, referencedNames: [],
        resourceIcon: { path: "", color: "", tooltip: "" }, stateIcon: { path: "", color: "", tooltip: "Starting" },
    };
    const resources = [
        { ...resource, name: "shared", displayName: "shared", tags: createTags("Backend", "API") },
        { ...resource, name: "worker", displayName: "worker", tags: createTags("Backend") },
        { ...resource, name: "other-api", displayName: "other-api", tags: createTags("API") },
    ];
    graph.updateNodes(resources);
    const [shared, worker, otherApi] = graph.nodes;
    for (const [node, x, y] of [[shared, 25, 50], [worker, 150, 100], [otherApi, -50, 0]]) {
        Object.assign(node, { x, y, vx: 0, vy: 0, bounds: { x: -56, y: -56, width: 112, height: 112 } });
    }
    graph.updateTagGroups();
    const group = () => graph.tagGroups.find(tag => tag.key === "BACKEND");
    const points = () => Array.from(graph.nodes, node => [node.x, node.y]);
    const pins = () => Array.from(graph.nodes, node => [node.fx, node.fy]);
    const hull = () => Array.from(graph.getTagHull(group()), point => [...point]);
    const startHull = hull();
    const drag = graph.tagControls.select("text").calledBehavior;
    const subject = drag.subjectAccessor({ x: 10, y: 20 }, group());
    drag.handlers.get("start")({ subject });
    assert.deepEqual(pins(), [[25, 50], [150, 100], [undefined, undefined]]);

    drag.handlers.get("drag")({ subject, x: 70, y: 5 });
    assert.deepEqual(points(), [[85, 35], [210, 85], [-50, 0]]);
    assert.deepEqual(hull(), startHull.map(([x, y]) => [x + 60, y - 15]));

    graph.updateNodes(resources.map(r => ({ ...r, stateIcon: { ...r.stateIcon, tooltip: "Running" } })));
    graph.updateTagGroups();
    drag.handlers.get("drag")({ subject, x: 40, y: 65 });
    assert.deepEqual(points(), [[55, 95], [180, 145], [-50, 0]]);
    assert.deepEqual(hull(), startHull.map(([x, y]) => [x + 30, y + 45]));
    assert.deepEqual(pins(), [[55, 95], [180, 145], [undefined, undefined]]);

    drag.handlers.get("end")({ subject });
    assert.deepEqual(pins(), [[null, null], [null, null], [undefined, undefined]]);
    assert.deepEqual(points(), [[55, 95], [180, 145], [-50, 0]]);
    assert.equal(graph.simulation.target, 0);
});

test("a stationary press and release on a tag label leaves its members free without reheating the simulation", () => {
    const graph = createGraph();
    const node = { id: "worker", x: 30, y: 40, tags: createTags("Backend") };
    graph.nodes = [node];
    graph.updateTagGroups();
    const drag = graph.tagControls.select("text").calledBehavior;
    const subject = drag.subjectAccessor({ x: 10, y: 20 }, graph.tagGroups[0]);
    drag.handlers.get("start")({ subject });
    drag.handlers.get("end")({ subject });
    assert.deepEqual([node.x, node.y, node.fx, node.fy], [30, 40, null, null]);
    assert.equal(graph.simulation.target, undefined);
});

test("link masks follow nodes outside the viewport and selected scaling", () => {
    const graph = createGraph();
    const left = { id: "left", x: -10000, y: 200 };
    const middle = { id: "middle", x: 0, y: 200 };
    const right = { id: "right", x: 10000, y: 200 };
    graph.nodes = [left, middle, right];
    graph.links = [{ source: left, target: right }];
    graph.updateLinkMask();

    assert.deepEqual([...graph.linkMaskNodes.values], [left, middle, right]);
    const normalRadius = graph.linkMaskNodes.attributes.get("r")(middle);
    graph.switchTo("middle");
    const radius = graph.linkMaskNodes.attributes.get("r");
    assert.ok(radius(middle) > normalRadius);
    assert.equal(radius(left), normalRadius);

    middle.x = 20000;
    middle.y = -15000;
    graph.updateLinkMask();
    assert.equal(graph.linkMaskNodes.attributes.get("cx")(middle), middle.x);
    assert.equal(graph.linkMaskNodes.attributes.get("cy")(middle), middle.y);
    for (const element of [graph.linkMask, graph.linkMaskBackground]) {
        const x = element.attributes.get("x");
        const y = element.attributes.get("y");
        const rightEdge = x + element.attributes.get("width");
        const bottomEdge = y + element.attributes.get("height");
        for (const node of graph.nodes) {
            assert.ok(x <= node.x - radius(node) && rightEdge >= node.x + radius(node));
            assert.ok(y <= node.y - radius(node) && bottomEdge >= node.y + radius(node));
        }
    }

    graph.switchTo(null);
    assert.equal(graph.linkMaskNodes.attributes.get("r")(middle), normalRadius);
    graph.nodes = [left, right];
    graph.updateLinkMask();
    assert.deepEqual([...graph.linkMaskNodes.values], [left, right]);
    graph.nodes = [];
    graph.updateLinkMask();
    assert.equal(graph.linkMaskNodes.values.length, 0);
    assert.ok(graph.linkMask.attributes.get("width") > 0);
    assert.ok(graph.linkMask.attributes.get("height") > 0);
});

test("changing embedded icon data invalidates graph icons", () => {
    const graph = createGraph();
    const icon = { path: "", color: "red", tooltip: "app", imageData: "data:image/png;base64,AQID" };
    assert.equal(graph.iconEqual(icon, { ...icon }), true);
    assert.equal(graph.iconEqual(icon, { ...icon, imageData: "data:image/png;base64,BAUG" }), false);
    assert.equal(graph.iconEqual(icon, { ...icon, imageData: undefined, path: "M0 0" }), false);
});

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph.js", import.meta.url), "utf8");

class Selection {
    attributes = new Map();

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
    attr(name, value) { this.attributes.set(name, value); return this; }
    call(fn, ...args) { this.environment?.calls.push({ fn, args }); return this; }
    on() { return this; }
    node() { return this.element; }
    transition() { return this; }
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
function createGraph(environment = null) {
    const behavior = {
        id() { return this; },
        strength() { return this; },
        distance() { return this; },
        iterations() { return this; },
        scaleExtent() { return this; },
        on() { return this; },
        force() { return this; },
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
            d3: {
                select: selector => environment && selector === ".resource-graph"
                    ? new Selection({ parentElement: environment.viewport }, environment)
                    : new Selection(),
                zoomIdentity: zoomTransform(0, 0, 1),
                zoom: () => behavior,
                drag: () => behavior,
                forceLink: () => behavior,
                forceSimulation: () => behavior,
                forceManyBody: () => behavior,
                forceCollide: () => behavior,
                forceX: () => behavior,
                forceY: () => behavior,
                forceCenter: () => behavior,
            },
        });
    return new ResourceGraph({ invokeMethodAsync: async () => {} }, null);
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
    assert.equal(graph.hasFitInitialGraph, false);
    assert.equal(graph.svg.attributes.has("viewBox"), false);

    // The graph is shown. Its first resize sizes the viewBox from the drawing area and fits the graph: the 200x100
    // content fits at the maximum scale of 1, centered on its middle (100, 50).
    environment.calls.length = 0;
    environment.viewport.clientWidth = 800;
    environment.viewport.clientHeight = 600;
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-400, -300, 800, 600]);
    assert.equal(graph.hasFitInitialGraph, true);
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
    assert.equal(graph.hasFitInitialGraph, false);
    assert.deepEqual(appliedZoomTransforms(environment), []);

    // Hiding the graph (another tab or overview mode) leaves it without a size. Keep the last viewBox for when it's shown.
    environment.viewport.clientWidth = 0;
    environment.viewport.clientHeight = 0;
    graph.resize();
    assert.deepEqual([...graph.svg.attributes.get("viewBox")], [-400, -300, 800, 600]);
});

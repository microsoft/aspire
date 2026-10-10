// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph.js", import.meta.url), "utf8");

class Selection {
    attributes = new Map();

    append() { return new Selection(); }
    selectAll() { return new Selection(); }
    attr(name, value) { this.attributes.set(name, value); return this; }
    call() { return this; }
    on() { return this; }
}

function createGraph() {
    const behavior = {
        id() { return this; },
        strength() { return this; },
        distance() { return this; },
        iterations() { return this; },
        scaleExtent() { return this; },
        on() { return this; },
        force() { return this; },
    };
    // The browser module imports D3 for its global side effect and exports interop entry points.
    // Supply only the D3 construction API here; the actual selection/highlight logic runs unchanged.
    const ResourceGraph = runInNewContext(
        source.replace(/^import '\.\/d3\.v7\.min\.js'\r?$/m, "").replace(/^export /gm, "") + "\nResourceGraph;",
        {
            d3: {
                select: () => new Selection(),
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

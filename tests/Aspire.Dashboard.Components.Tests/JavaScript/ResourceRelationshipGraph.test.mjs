// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const context = { setTimeout, clearTimeout, setInterval, clearInterval };
runInNewContext(await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/d3.v7.min.js", import.meta.url), "utf8"), context);
const source = await readFile(new URL("../../../src/Aspire.Dashboard/Components/Controls/ResourceRelationshipGraph.razor.js", import.meta.url), "utf8");
// The browser module imports the existing D3 bundle for its global side effect. Run that same bundle here.
const createLayout = new Function("d3", source.replace(/^import .*;\r?$/m, "").replace(/^export /gm, "") +
    "\nreturn createLayout;")(context.d3);

for (const mode of ["Flow", "Hierarchy", "Topology"]) {
    const resource = { name: "catalog-api", type: "Project" };
    const incoming = [{ name: "catalog-api-child", type: "Parent" }, { name: "catalog-api-frontend", type: "Reference" }];
    const outgoing = Array.from({ length: 16 }, (_, i) =>
        ({ name: `catalog-api-dependency-${String(i + 1).padStart(2, "0")}`, type: "Reference" }))
        .concat([{ name: "catalog-api-platform", type: "Parent" },
            { name: "catalog-api-dependency-01", type: "WaitFor" }, { name: "catalog-api-frontend", type: "Reference" }]);
    test(`${mode} keeps ordinary fan-out boxes close to label-sized boxes`, () => {
        const layout = createLayout(mode, 710, resource, incoming, outgoing, text => text.length * 7);
        const selected = layout.nodes.find(node => node.isCenter);
        const neighbor = layout.nodes.find(node => !node.isCenter);
        assert.equal(selected.width, neighbor.width);
        assert.ok(selected.height <= neighbor.height + 16,
            `Selected box ${selected.width}x${selected.height}, ordinary box ${neighbor.width}x${neighbor.height}`);
    });
    test(`${mode} gives every arrow a straight aligned 16px join after its curve`, () => {
        const layout = createLayout(mode, 710, resource, incoming, outgoing, text => text.length * 7);
        for (const edge of layout.edges) {
            const commands = edge.path.match(/[MLCQ][^MLCQ]*/g);
            assert.equal(commands.at(-1)[0], "L", edge.path);
            const values = command => command.slice(1).match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
            const [x, y] = values(commands.at(-2)).slice(-2);
            const [endX, endY] = values(commands.at(-1));
            const target = layout.nodes.find(node => node.node.name === edge.target);
            assert.ok(x === endX || y === endY, edge.path);
            assert.ok(Math.hypot(endX - x, endY - y) >= 16 - .001, edge.path);
            const horizontal = endX < target.x || endX > target.x + target.width;
            assert.equal(horizontal ? y : x, horizontal ? endY : endX, edge.path);
        }
    });
}

for (const mode of ["Flow", "Hierarchy", "Topology"]) {
    for (const width of [331, 710, 1092]) {
        for (const count of [2, 16, 32, 64, 96]) {
            test(`${mode} at ${width}px preserves ${count} long-name dependencies and separate directed routes`, () => {
                const name = "regional-payment-authorization-api";
                const resource = { name, type: "Project" };
                const incoming = [{ name: `${name}-child`, type: "Parent" }, { name: `${name}-frontend`, type: "Reference" }];
                const outgoing = Array.from({ length: count }, (_, i) => ({ name: `${name}-dependency-${String(i + 1).padStart(2, "0")}`, type: "Reference" }))
                    .concat([{ name: `${name}-platform`, type: "Parent" }, { name: `${name}-dependency-01`, type: "WaitFor" },
                        { name: `${name}-frontend`, type: "Reference" }]).sort((a, b) => a.name.localeCompare(b.name));
                const layout = createLayout(mode, width, resource, incoming, outgoing, text => text.length * 7);
                assert.equal(layout.nodes.length, count + 4);
                assert.equal(layout.edges.length, count + 5);
                if (mode === "Flow" && width >= 710) assert.ok(layout.width <= width);
                for (const node of layout.nodes) {
                    assert.equal(node.lines.join(""), node.node.name);
                    assert.ok(node.lines.every(line => line.length * 7 <= node.width - 36));
                    if (node.node.name.includes("dependency")) assert.ok(node.lines.some(line => line.includes("dependency")));
                    assert.ok(node.x >= 0 && node.y >= 0);
                    assert.ok(node.x + node.width <= layout.width && node.y + node.height <= layout.height);
                }
                for (let i = 0; i < layout.nodes.length; i++) {
                    for (let j = i + 1; j < layout.nodes.length; j++) {
                        const a = layout.nodes[i], b = layout.nodes[j];
                        assert.ok(a.x >= b.x + b.width || a.x + a.width <= b.x ||
                            a.y >= b.y + b.height || a.y + a.height <= b.y, `${a.node.name} overlaps ${b.node.name}`);
                    }
                }
                const identity = edge => `${edge.source}|${edge.target}|${edge.type}`;
                const expected = [...incoming.map(node => `${node.name}|${name}|${node.type}`),
                    ...outgoing.map(node => `${name}|${node.name}|${node.type}`)].sort();
                assert.equal(JSON.stringify(layout.edges.map(identity).sort()), JSON.stringify(expected));
                if (mode === "Hierarchy") {
                    const selected = layout.nodes.find(node => node.isCenter);
                    const upper = layout.nodes.filter(node => node.y < selected.y);
                    const lower = layout.nodes.filter(node => node.y > selected.y);
                    assert.equal(new Set(upper.map(node => node.y)).size, 1);
                    assert.equal(new Set(lower.map(node => node.y)).size, 1);
                    assert.equal(upper.length, 2);
                    assert.equal(lower.length, count + 1);
                    assert.equal(layout.height, selected.height * 3 + 96 + Math.max(96, (count + 1) * 8 + 56) + 32);
                }
                const occupied = new Map();
                for (const node of layout.nodes) {
                    const tips = layout.edges.filter(edge => edge.target === node.node.name).map(edge => edge.points.at(-1));
                    for (let i = 0; i < tips.length; i++) {
                        for (let j = i + 1; j < tips.length; j++) {
                            assert.ok(Math.hypot(tips[i][0] - tips[j][0], tips[i][1] - tips[j][1]) >= 16);
                        }
                    }
                }
                for (const edge of layout.edges) {
                    assert.ok(!edge.path.includes("NaN"));
                    if (mode !== "Topology") {
                        assert.ok(edge.path.includes("C"));
                        assert.equal(edge.points.length, 2);
                        continue;
                    }
                    for (let i = 1; i < edge.points.length; i++) {
                        const [x, y] = edge.points[i - 1], [endX, endY] = edge.points[i];
                        assert.ok(x === endX || y === endY);
                        for (const node of layout.nodes) {
                            assert.ok(!(x === endX && x > node.x && x < node.x + node.width &&
                                Math.max(y, endY) > node.y && Math.min(y, endY) < node.y + node.height) &&
                                !(y === endY && y > node.y && y < node.y + node.height &&
                                Math.max(x, endX) > node.x && Math.min(x, endX) < node.x + node.width),
                            `${edge.id} crosses ${node.node.name}`);
                        }
                        const dx = Math.sign(endX - x), dy = Math.sign(endY - y);
                        for (let a = x, b = y; a !== endX || b !== endY; a += dx, b += dy) {
                            const ends = [`${a},${b}`, `${a + dx},${b + dy}`].sort();
                            const key = ends.join("|");
                            assert.equal(occupied.has(key), false, `Shared or retraced segment ${key}: ${occupied.get(key)} / ${edge.id}`);
                            occupied.set(key, edge.id);
                        }
                    }
                }
                const repeated = createLayout(mode, width, resource, incoming, outgoing, text => text.length * 7);
                assert.equal(JSON.stringify(repeated), JSON.stringify(layout));
            });
        }
    }
}

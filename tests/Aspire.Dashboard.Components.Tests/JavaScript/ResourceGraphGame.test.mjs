// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { test } from "node:test";
import {
    enemyPoints,
    attachResourceGraphGame,
    buildFormation,
    firewallCollision,
    createFirewall,
    damageFirewall,
    detectCircleGesture,
    eraseFirewall,
    extraLives,
    marchInterval,
    MAX_ENEMIES,
    padScore,
    readBestScore,
    rectsOverlap,
    saveBestScore,
    textWidth,
} from "../../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph-game.js";

// Samples a circle (or a spiral when endRadius differs) the way throttled pointermove events would arrive.
function circle({ turns, radius = 60, endRadius = radius, cx = 400, cy = 300, pointsPerTurn = 40, clockwise = false }) {
    const points = [];
    const count = Math.round(turns * pointsPerTurn);
    for (let i = 0; i <= count; i++) {
        const t = i / pointsPerTurn;
        const angle = (clockwise ? -1 : 1) * t * Math.PI * 2;
        const r = radius + ((endRadius - radius) * (i / count));
        points.push({ x: cx + (Math.cos(angle) * r), y: cy + (Math.sin(angle) * r), t: i * 16 });
    }
    return points;
}

function line(count, from, to) {
    return Array.from({ length: count }, (_, i) => ({
        x: from.x + ((to.x - from.x) * (i / (count - 1))),
        y: from.y + ((to.y - from.y) * (i / (count - 1))),
        t: i * 16,
    }));
}

test("two full circles are recognized in either direction", () => {
    assert.equal(detectCircleGesture(circle({ turns: 2 })), true);
    assert.equal(detectCircleGesture(circle({ turns: 2.3, clockwise: true })), true);
});

test("a loose spiral counts as circles", () => {
    assert.equal(detectCircleGesture(circle({ turns: 2.2, radius: 45, endRadius: 85 })), true);
});

test("circles preceded by unrelated movement are recognized", () => {
    const approach = line(30, { x: 0, y: 0 }, { x: 460, y: 300 });
    assert.equal(detectCircleGesture([...approach, ...circle({ turns: 2.1 })]), true);
});

test("ordinary pointer movement is not a gesture", () => {
    assert.equal(detectCircleGesture(circle({ turns: 1.5 })), false, "one and a half circles");
    assert.equal(detectCircleGesture(line(80, { x: 0, y: 0 }, { x: 600, y: 400 })), false, "straight line");
    assert.equal(detectCircleGesture(circle({ turns: 3, radius: 6 })), false, "tiny jitter");

    const zigzag = Array.from({ length: 80 }, (_, i) => ({ x: i * 8, y: (i % 2) * 40, t: i * 16 }));
    assert.equal(detectCircleGesture(zigzag), false, "zigzag");

    // Sweeping back and forth along the same arc covers a lot of angle but no net turn.
    const backAndForth = Array.from({ length: 120 }, (_, i) => {
        const angle = Math.abs(((i % 40) / 20) - 1) * Math.PI;
        return { x: 400 + (Math.cos(angle) * 60), y: 300 + (Math.sin(angle) * 60), t: i * 16 };
    });
    assert.equal(detectCircleGesture(backAndForth), false, "back and forth");

    assert.equal(detectCircleGesture([]), false, "no points");
});

test("formation repeats tiny graphs and grows with waves", () => {
    const one = buildFormation(1, 1);
    assert.equal(one.slots.length, 15);
    assert.ok(one.slots.every(slot => slot.resourceIndex === 0));

    const two = buildFormation(2, 1);
    assert.deepEqual(new Set(two.slots.map(slot => slot.resourceIndex)), new Set([0, 1]));

    assert.ok(buildFormation(2, 3).slots.length > two.slots.length);
    assert.equal(buildFormation(2, 50).slots.length, MAX_ENEMIES);
});

test("formation includes every resource of a larger graph", () => {
    const formation = buildFormation(30, 1);
    assert.equal(formation.slots.length, 30);
    assert.deepEqual(formation.slots.map(slot => slot.resourceIndex), Array.from({ length: 30 }, (_, i) => i));
    assert.ok(formation.columns <= 11);
    assert.equal(formation.rows, Math.ceil(30 / formation.columns));
});

test("formation centers a partial last row", () => {
    const formation = buildFormation(1, 1);
    const lastRow = formation.slots.filter(slot => slot.row === formation.rows - 1);
    const inRow = lastRow.length;
    assert.ok(inRow < formation.columns);
    assert.equal(lastRow[0].col, (formation.columns - inRow) / 2);
});

test("top rows score more than bottom rows", () => {
    assert.deepEqual([0, 1, 2].map(row => enemyPoints(row, 3)), [40, 25, 15]);
    assert.equal(enemyPoints(0, 1), 40);
    assert.equal(enemyPoints(5, 6), 15);
});

test("march speeds up as enemies die and as waves advance", () => {
    assert.ok(marchInterval(1, 20, 1) < marchInterval(10, 20, 1));
    assert.ok(marchInterval(10, 20, 1) < marchInterval(20, 20, 1));
    assert.ok(marchInterval(20, 20, 3) < marchInterval(20, 20, 1));
    assert.ok(marchInterval(0, 20, 99) >= 0.03);
});

test("extra lives are awarded every 2500 points", () => {
    assert.equal(extraLives(0, 2490), 0);
    assert.equal(extraLives(2490, 2520), 1);
    assert.equal(extraLives(2400, 5100), 2);
});

test("rectangles overlap only when they intersect", () => {
    const a = { x: 0, y: 0, w: 10, h: 10 };
    assert.equal(rectsOverlap(a, { x: 9, y: 9, w: 2, h: 2 }), true);
    assert.equal(rectsOverlap(a, { x: 10, y: 0, w: 2, h: 2 }), false);
    assert.equal(rectsOverlap(a, { x: -3, y: 4, w: 2, h: 2 }), false);
});

test("firewalls block shots until a hole is worn through", () => {
    const firewall = createFirewall(100, 200);

    // Row 0 has crenellations: columns 3 and 4 are open, column 5 is solid.
    assert.equal(firewallCollision(firewall, { x: 103, y: 200, w: 2, h: 1 }), null);
    assert.deepEqual(firewallCollision(firewall, { x: 105, y: 200, w: 1, h: 1 }), { x: 105, y: 200 });
    assert.equal(firewallCollision(firewall, { x: 50, y: 50, w: 4, h: 4 }), null);

    const column = { x: 110, y: 200, w: 1, h: 10 };
    let hits = 0;
    let hit;
    while ((hit = firewallCollision(firewall, column)) && hits < 50) {
        assert.ok(damageFirewall(firewall, hit.x, hit.y, 2.5, () => 0) > 0);
        hits++;
    }
    assert.ok(hits > 1 && hits < 50, `wore through after ${hits} hits`);
    assert.equal(firewall.dirty, true);
});

test("formation erases firewall cells it marches through", () => {
    const firewall = createFirewall(0, 0);
    assert.ok(eraseFirewall(firewall, { x: 0, y: 4, w: 20, h: 2 }) > 0);
    assert.equal(firewallCollision(firewall, { x: 0, y: 4, w: 20, h: 2 }), null);
});

test("best score survives missing or failing storage", () => {
    const items = new Map();
    const storage = { getItem: key => items.get(key) ?? null, setItem: (key, value) => items.set(key, value) };

    assert.equal(readBestScore(() => storage), 0);
    assert.equal(saveBestScore(() => storage, 1200), 1200);
    assert.equal(saveBestScore(() => storage, 800), 1200);
    assert.equal(readBestScore(() => storage), 1200);

    items.set("aspire-resource-graph-game-best-score", "not a number");
    assert.equal(readBestScore(() => storage), 0);

    const throwing = () => { throw new Error("SecurityError"); };
    assert.equal(readBestScore(throwing), 0);
    assert.equal(saveBestScore(throwing, 500), 500);
    assert.equal(readBestScore(() => null), 0);
});

test("scores are zero padded and text is measured in font pixels", () => {
    assert.equal(padScore(120), "000120");
    assert.equal(textWidth("AB"), 11);
    assert.equal(textWidth("AB", 2), 22);
    assert.equal(textWidth(""), 0);
});

test("attaching is a no-op without a rendered graph", () => {
    attachResourceGraphGame(null);
    attachResourceGraphGame({ svg: { node: () => undefined } });

    const listeners = [];
    const svg = { addEventListener: (type) => listeners.push(type) };
    const graph = { svg: { node: () => svg } };
    attachResourceGraphGame(graph);
    attachResourceGraphGame(graph);
    assert.deepEqual(listeners, ["pointermove", "pointerleave"]);
});

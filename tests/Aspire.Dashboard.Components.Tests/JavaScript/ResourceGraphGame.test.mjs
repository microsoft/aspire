// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { test } from "node:test";
import {
    enemyPoints,
    attachResourceGraphGame,
    buildFormation,
    computeWorldSize,
    firewallCollision,
    createFirewall,
    damageFirewall,
    eraseFirewall,
    extraLives,
    isButtonTarget,
    isLaunchKey,
    isPausable,
    marchInterval,
    MAX_ENEMIES,
    padScore,
    readBestScore,
    ResourceOutage,
    rectsOverlap,
    saveBestScore,
    textWidth,
} from "../../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph-game.js";

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
    attachResourceGraphGame({ svg: { node: () => ({ closest: () => null }) } });
});

test("attaching wires the play button and the launch key once", () => {
    const containerListeners = [];
    const buttonListeners = [];
    const button = { addEventListener: (type) => buttonListeners.push(type) };
    const container = {
        addEventListener: (type) => containerListeners.push(type),
        querySelector: (selector) => selector === ".graph-game" ? button : null,
    };
    const graph = { svg: { node: () => ({ closest: () => container }) } };
    const svg = graph.svg.node();
    graph.svg.node = () => svg;
    attachResourceGraphGame(graph);
    attachResourceGraphGame(graph);
    assert.deepEqual(containerListeners, ["keydown"]);
    assert.deepEqual(buttonListeners, ["click"]);
});

function outageWith(results = () => true) {
    const calls = [];
    const outage = new ResourceOutage(async (id, command) => {
        calls.push(`${command} ${id}`);
        return results(id, command, calls);
    }, () => { }, 0);
    return { outage, calls };
}

test("hard core stops a resource once however many of its enemies are destroyed", async () => {
    const { outage, calls } = outageWith();
    assert.equal(outage.stop("api"), true);
    assert.equal(outage.stop("api"), false);
    assert.equal(outage.stop("cache"), true);
    await outage.whenIdle();
    assert.equal(outage.downCount, 2);
    assert.deepEqual(calls, ["stop api", "stop cache"]);
});

test("hard core restarts what it stopped when the wave is cleared, after the stop has finished", async () => {
    const { outage, calls } = outageWith();
    outage.stop("api");
    outage.restartAll();
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
    assert.deepEqual(calls, ["stop api", "start api"]);

    // The next wave can stop it again.
    assert.equal(outage.stop("api"), true);
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api", "start api", "stop api"]);
});

test("hard core tries again until a command becomes available", async () => {
    let starts = 0;
    const { outage, calls } = outageWith((_, command) => command === "stop" || ++starts === 3 ? true : null);
    outage.stop("api");
    outage.restartAll();
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api", "start api", "start api", "start api"]);
});

test("hard core doesn't count or restart a resource whose stop failed, and doesn't try it again", async () => {
    const { outage, calls } = outageWith((_, command) => command === "stop" ? false : true);
    outage.stop("api");
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);

    // Another enemy of the same resource, in the same wave.
    assert.equal(outage.stop("api"), false);
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api"]);

    outage.restartAll();
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api"]);

    // The next wave gets its own attempt.
    assert.equal(outage.stop("api"), true);
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api", "stop api"]);
});

test("a new game tries a failed stop again and still restarts what the last game left down", async () => {
    let apiStops = 0;
    const { outage, calls } = outageWith((id, command) => id !== "api" || command !== "stop" || ++apiStops === 2);
    outage.stop("api");
    outage.stop("cache");
    await outage.whenIdle();
    assert.equal(outage.downCount, 1);

    outage.forgetAttempts();
    assert.equal(outage.downCount, 1);
    assert.equal(outage.stop("api"), true);
    await outage.whenIdle();
    assert.equal(outage.downCount, 2);

    outage.restartAll();
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
    assert.deepEqual(calls.filter(call => call.startsWith("start")).sort(), ["start api", "start cache"]);
});

test("hard core reports changes when a command finishes late", async () => {
    let finishStart;
    let changes = 0;
    const outage = new ResourceOutage(
        (_, command) => command === "stop" ? Promise.resolve(true) : new Promise(resolve => { finishStart = resolve; }),
        () => { changes++; },
        0);
    outage.stop("api");
    await outage.queues.get("api");
    assert.equal(changes, 1);

    // The game can reach its game over screen while this start is running.
    outage.restartAll();
    await new Promise(resolve => setTimeout(resolve));
    assert.equal(outage.downCount, 1);
    assert.equal(changes, 1);

    finishStart(true);
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
    assert.equal(changes, 2);
});

test("hard core keeps counting a resource whose start failed, and tries it when the next wave is cleared", async () => {
    let starts = 0;
    const { outage, calls } = outageWith((_, command) => command === "stop" || ++starts === 2);
    outage.stop("api");
    outage.restartAll();
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api", "start api"]);
    assert.equal(outage.downCount, 1);

    outage.restartAll();
    await outage.whenIdle();
    assert.deepEqual(calls, ["stop api", "start api", "start api"]);
    assert.equal(outage.downCount, 0);
});

test("hard core counts a resource as down until its start has succeeded", async () => {
    const { outage } = outageWith();
    outage.stop("api");
    await outage.whenIdle();
    outage.restartAll();
    assert.equal(outage.downCount, 1);
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
});

test("hard core leaves alone a resource it couldn't stop", async () => {
    const { outage, calls } = outageWith((_, command) => command === "stop" ? null : true);
    outage.stop("already-stopped");
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
    outage.restartAll();
    await outage.whenIdle();
    assert.equal(calls.filter(call => call.startsWith("start")).length, 0);
    assert.equal(calls.length, 10);
});

test("hard core only counts and announces a resource once its stop has succeeded", async () => {
    let finishStop;
    let stopped = 0;
    const outage = new ResourceOutage(() => new Promise(resolve => { finishStop = resolve; }), () => { }, 0);
    outage.stop("api", () => { stopped++; });
    await new Promise(resolve => setTimeout(resolve));
    assert.equal(outage.downCount, 0);
    assert.equal(stopped, 0);

    finishStop(true);
    await outage.whenIdle();
    assert.equal(outage.downCount, 1);
    assert.equal(stopped, 1);
});

test("hard core gives up waiting stops when the game closes, but not starts", async () => {
    const { outage, calls } = outageWith((_, command, list) => command === "stop" && list.length < 3 ? null : true);
    outage.stop("api");
    outage.stop("cache");
    await new Promise(resolve => setTimeout(resolve));
    outage.cancelPendingStops();
    await outage.whenIdle();
    assert.ok(calls.length < 10, "stops were tried again after the game closed");

    const running = outageWith();
    running.outage.stop("api");
    await running.outage.whenIdle();
    running.outage.cancelPendingStops();
    running.outage.restartAll();
    await running.outage.whenIdle();
    assert.deepEqual(running.calls, ["stop api", "start api"]);
});

test("hard core logs a start it could never run", async (t) => {
    const warn = t.mock.method(console, "warn", () => { });
    const { outage } = outageWith((_, command) => command === "stop" ? true : null);
    outage.stop("api");
    outage.restartAll();
    await outage.whenIdle();
    assert.equal(warn.mock.callCount(), 1);
    assert.equal(outage.downCount, 1);
});

test("hard core survives a page that went away", async () => {
    const outage = new ResourceOutage(async () => { throw new Error("circuit disconnected"); }, () => { }, 0);
    outage.stop("api");
    await outage.whenIdle();
    assert.equal(outage.downCount, 0);
});

test("the largest formation fits and has room to move in any graph shape", () => {
    const formation = buildFormation(MAX_ENEMIES, 1);
    // SPRITE is 12, SPACING_X and SPACING_Y are 18, the formation starts at y=28 and at most 20 lower on later waves, the
    // firewalls start 49 above the bottom edge.
    const formationWidth = ((formation.columns - 1) * 18) + 12;
    const formationBottom = 28 + 20 + ((formation.rows - 1) * 18) + 14;

    for (const [width, height] of [[940, 300], [360, 600], [1800, 900], [300, 200], [2400, 180]]) {
        const world = computeWorldSize(width, height);
        assert.ok(world.width - formationWidth >= 40, `${width}x${height}: no room to march in ${world.width}`);
        assert.ok(world.height - 49 - formationBottom >= 36, `${width}x${height}: no room to descend in ${world.height}`);
        // Same scale on both axes: the sprites aren't stretched.
        assert.ok(Math.abs((width / world.width) - (height / world.height)) < 0.05, `${width}x${height}: distorted`);
    }
});

test("a large graph keeps big pixels", () => {
    assert.deepEqual(computeWorldSize(1680, 1200), { width: 420, height: 300 });
});

test("the launch key only applies to the graph itself", () => {
    const closest = (selector) => (selector.includes("button") ? {} : null);
    assert.equal(isLaunchKey({ key: "o", target: {} }), true);
    assert.equal(isLaunchKey({ key: "O", target: {} }), true);
    assert.equal(isLaunchKey({ key: "x", target: {} }), false);
    assert.equal(isLaunchKey({ key: "o", repeat: true, target: {} }), false);
    assert.equal(isLaunchKey({ key: "o", ctrlKey: true, target: {} }), false);
    assert.equal(isLaunchKey({ key: "o", target: { closest } }), false);
    assert.equal(isLaunchKey({ key: "o", target: { closest: (selector) => selector.split(", ").includes("fluent-button") ? {} : null } }), false);
});

test("Enter and Space stay with a focused button", () => {
    assert.equal(isButtonTarget({ closest: () => ({}) }), true);
    assert.equal(isButtonTarget({ closest: () => null }), false);
    assert.equal(isButtonTarget(null), false);
});

test("the game can pause in every state that would otherwise carry on by itself", () => {
    for (const state of ["ready", "playing", "dying", "cleared"]) {
        assert.equal(isPausable(state), true, state);
    }
    for (const state of ["morph-in", "title", "gameover", "paused", "morph-out"]) {
        assert.equal(isPausable(state), false, state);
    }
});

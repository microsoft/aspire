// Rock Paper Scissors Arena — Aspire TypeScript AppHost
// A polyglot game: C# Game Master, Python & Node.js players, React frontend, PostgreSQL

import { createBuilder, ContainerLifetime, type ExecuteCommandContext, type ExecuteCommandResult } from './.modules/aspire.js';

const builder = await createBuilder();

// ── Database ──────────────────────────────────────────────
// PostgreSQL stores game rounds and leaderboard data.
// Persistent lifetime so data survives restarts during development.
const postgres = await builder
    .addPostgres("postgres")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume();

const gameDb = await postgres.addDatabase("gamedb");

// ── Python Serpent (Player 1) ─────────────────────────────
// A cunning Python player that uses pattern analysis.
const pythonPlayer = await builder
    .addPythonApp("python-player", "./python-player", "app.py")
    .withHttpEndpoint({ env: "PORT" })
    .withHttpHealthCheck({ path: "/health" });

// ── Node Knight (Player 2) ────────────────────────────────
// A noble Node.js player with adaptive strategies.
// Uses tsx watch for hot-reload during development.
const nodePlayer = await builder
    .addNodeApp("node-player", "./node-player", "src/server.ts")
    .withRunScript("dev")
    .withHttpEndpoint({ env: "PORT" })
    .withHttpHealthCheck({ path: "/health" });

// ── C# Paladin (Player 3) ─────────────────────────────────
// A disciplined C# player that uses probability theory and streak detection.
const csharpPlayer = await builder
    .addCSharpApp("csharp-player", "./csharp-player/csharp-player.cs")
    .withHttpEndpoint()
    .withHttpHealthCheck({ path: "/health" });

// ── Game Master (C# file-based API) ──────────────────────
// Orchestrates matches, calls all players, stores results.
// Waits for the database and all players before starting.
const gamemaster = await builder
    .addCSharpApp("gamemaster", "./gamemaster/gamemaster.cs")
    .withReference(gameDb)
    .withReference(pythonPlayer)
    .withReference(nodePlayer)
    .withReference(csharpPlayer)
    .waitFor(gameDb)
    .waitFor(pythonPlayer)
    .waitFor(nodePlayer)
    .waitFor(csharpPlayer)
    .withHttpEndpoint()
    .withHttpHealthCheck({ path: "/health" })
    .withCommand("clear-history", "Clear History", async (context: ExecuteCommandContext): Promise<ExecuteCommandResult> => {
        const endpoint = await gamemaster.getEndpoint("http");
        const url = await endpoint.url.get();
        const res = await fetch(`${url}/api/rounds`, { method: "DELETE" });
        if (res.ok) {
            return { success: true };
        }
        return { success: false, errorMessage: `Failed to clear history: ${res.statusText}` };
    }, {
        commandOptions: {
            description: "Clear all battle history and leaderboard data",
            confirmationMessage: "Are you sure you want to clear all battle history?",
            iconName: "Delete",
        }
    });

// ── Arena Frontend (Vite + React) ─────────────────────────
// Live arena view with leaderboard and battle history.
// Proxies /api requests to the Game Master via Vite dev server.
await builder
    .addViteApp("arena", "./arena-frontend")
    .withReference(gamemaster)
    .waitFor(gamemaster)
    .withExternalHttpEndpoints()
    .withBrowserDebugger();

console.log("⚔️  RPS Arena configured");

await builder.build().run();

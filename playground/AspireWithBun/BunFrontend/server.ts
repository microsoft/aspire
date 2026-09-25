// Minimal Bun HTTP server used by the AspireWithBun playground and BunFunctionalTests.
// Distinguishes between direct invocation (`bun server.ts`) and package-script invocation
// (`bun run start`) by checking `npm_lifecycle_event`, which Bun sets when running a script.

const port = Number(process.env.PORT ?? 3000);
const isScriptRun = process.env.npm_lifecycle_event !== undefined;
const greeting = isScriptRun ? "Hello from bun script!" : "Hello from bun!";

const server = Bun.serve({
    port,
    fetch(_req) {
        return new Response(greeting, {
            headers: { "Content-Type": "text/plain" },
        });
    },
});

console.log(`Bun server listening on http://${server.hostname}:${server.port}`);

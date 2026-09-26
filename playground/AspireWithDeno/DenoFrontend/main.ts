// Minimal Deno HTTP server used by the AspireWithDeno playground and DenoFunctionalTests.
// Distinguishes between direct invocation (`deno run -A main.ts`) and task invocation
// (`deno task start`) by checking for the `--from-task` argument injected by the deno.json task.

const port = Number(Deno.env.get("PORT") ?? 3000);
const isTaskRun = Deno.args.includes("--from-task");
const greeting = isTaskRun ? "Hello from deno task!" : "Hello from deno!";

Deno.serve({ port }, () =>
    new Response(greeting, {
        headers: { "Content-Type": "text/plain" },
    }));

console.log(`Deno server listening on http://localhost:${port}`);

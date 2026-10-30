const port = Number(Deno.env.get("PORT") ?? "8000");

Deno.serve({ port }, () => {
    return new Response("Hello from Deno via Aspire TS integration");
});

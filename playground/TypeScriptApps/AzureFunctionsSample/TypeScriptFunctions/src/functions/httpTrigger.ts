import { app, HttpRequest, HttpResponseInit, InvocationContext } from "@azure/functions";

export async function httpTrigger(request: HttpRequest, context: InvocationContext): Promise<HttpResponseInit> {
    context.log("HTTP trigger function processed a request.");

    const name = request.query.get("name") || (await request.text()) || "World";

    return {
        body: `Hello, ${name}! This is an Aspire-hosted Azure Functions TypeScript app.`
    };
}

app.http("httpTrigger", {
    methods: ["GET", "POST"],
    authLevel: "anonymous",
    handler: httpTrigger,
});

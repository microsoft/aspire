import { app, HttpRequest, HttpResponseInit, InvocationContext, output } from "@azure/functions";

const samplesContainerName = "samples";
const sampleBlobOutput = output.storageBlob({
    path: `${samplesContainerName}/{rand-guid}.txt`,
    connection: "blobs"
});

export async function storageHttpTrigger(request: HttpRequest, context: InvocationContext): Promise<HttpResponseInit> {
    context.log("HTTP trigger writing a sample blob via output binding.");

    const name = request.query.get("name") || (await request.text()) || "Aspire";
    const content = `Hello, ${name}! Written by an Aspire-hosted TypeScript Azure Functions HTTP trigger.`;

    context.extraOutputs.set(sampleBlobOutput, content);

    return {
        jsonBody: {
            message: content,
            containerName: samplesContainerName
        }
    };
}

app.http("storageHttpTrigger", {
    methods: ["GET", "POST"],
    authLevel: "anonymous",
    extraOutputs: [sampleBlobOutput],
    handler: storageHttpTrigger
});

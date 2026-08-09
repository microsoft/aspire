import { app, InvocationContext, output } from "@azure/functions";

const blobOutput = output.storageBlob({
    connection: "blob",
    path: "test-files/{name}.txt",
});

export async function blobTrigger(blob: Buffer, context: InvocationContext): Promise<void> {
    const blobName = context.triggerMetadata?.name as string;
    context.log(`Blob trigger function invoked for 'myblobcontainer/${blobName}' with size ${blob.length} bytes`);

    const content = blob.toString();
    context.extraOutputs.set(blobOutput, content.toUpperCase());
}

app.storageBlob("blobTrigger", {
    path: "myblobcontainer/{name}",
    connection: "blob",
    extraOutputs: [blobOutput],
    handler: blobTrigger,
});

import { app, HttpRequest, HttpResponseInit, InvocationContext } from "@azure/functions";
import { DefaultAzureCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";
import { randomUUID } from "node:crypto";

export async function storageHttpTrigger(request: HttpRequest, context: InvocationContext): Promise<HttpResponseInit> {
    context.log("HTTP trigger writing a sample blob.");

    const connectionString = process.env.SAMPLES_CONNECTIONSTRING;
    const serviceUri = process.env.SAMPLES_URI;
    const containerName = process.env.SAMPLES_BLOBCONTAINERNAME;

    if ((!connectionString && !serviceUri) || !containerName) {
        return {
            status: 500,
            jsonBody: {
                error: "Azure Storage connection information was not provided.",
                expectedEnvironmentVariables: ["SAMPLES_CONNECTIONSTRING or SAMPLES_URI", "SAMPLES_BLOBCONTAINERNAME"]
            }
        };
    }

    const name = request.query.get("name") || (await request.text()) || "Aspire";
    // Aspire emits a connection string for local Azurite, and service URIs plus
    // managed identity settings for Azure deployment. DefaultAzureCredential honors
    // AZURE_CLIENT_ID/AZURE_TOKEN_CREDENTIALS when the app runs in Azure.
    // See: https://learn.microsoft.com/azure/developer/javascript/sdk/authentication/overview
    let blobServiceClient: BlobServiceClient;
    if (connectionString) {
        blobServiceClient = BlobServiceClient.fromConnectionString(connectionString);
    } else if (serviceUri) {
        blobServiceClient = new BlobServiceClient(serviceUri, new DefaultAzureCredential());
    } else {
        throw new Error("Expected Azure Storage connection information after validation.");
    }
    const containerClient = blobServiceClient.getContainerClient(containerName);
    await containerClient.createIfNotExists();

    const blobName = `samples/${Date.now()}-${randomUUID()}.txt`;
    const blockBlobClient = containerClient.getBlockBlobClient(blobName);
    const content = `Hello, ${name}! Written by an Aspire-hosted TypeScript Azure Functions app.`;
    await blockBlobClient.upload(content, Buffer.byteLength(content));

    return {
        jsonBody: {
            message: content,
            containerName,
            blobName
        }
    };
}

app.http("storageHttpTrigger", {
    methods: ["GET", "POST"],
    authLevel: "anonymous",
    handler: storageHttpTrigger
});

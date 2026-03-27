import express from "express";
import { QueueServiceClient } from "@azure/storage-queue";
import { BlobServiceClient } from "@azure/storage-blob";
import { EventHubProducerClient } from "@azure/event-hubs";
import crypto from "crypto";

const app = express();
const port = parseInt(process.env["PORT"] || "3000");

function randomString(length: number): string {
    const chars = "abcdefghijklmnopqrstuvwxyz";
    return Array.from(crypto.randomBytes(length))
        .map((b) => chars[b % chars.length])
        .join("");
}

app.get("/publish/asq", async (_req, res) => {
    try {
        const connectionString = process.env["ConnectionStrings__queue"] || "";
        const client = QueueServiceClient.fromConnectionString(connectionString);
        const queue = client.getQueueClient("queue");
        await queue.createIfNotExists();
        const data = Buffer.from("Hello, World!").toString("base64");
        await queue.sendMessage(data);
        res.send("Message sent to Azure Storage Queue.");
    } catch (err) {
        res.status(500).send(`Error publishing to queue: ${err}`);
    }
});

app.get("/publish/blob", async (_req, res) => {
    try {
        const connectionString = process.env["ConnectionStrings__blob"] || "";
        const client = BlobServiceClient.fromConnectionString(connectionString);
        const container = client.getContainerClient("myblobcontainer");
        await container.createIfNotExists();

        const entry = { id: crypto.randomUUID(), text: randomString(20) };
        const content = JSON.stringify(entry);
        const blob = container.getBlockBlobClient(entry.id);
        await blob.upload(content, content.length);

        res.send("String uploaded to Azure Storage Blobs.");
    } catch (err) {
        res.status(500).send(`Error uploading blob: ${err}`);
    }
});

app.get("/publish/eventhubs", async (_req, res) => {
    try {
        const connectionString = process.env["ConnectionStrings__myhub"] || "";
        const client = new EventHubProducerClient(connectionString, "myhub");
        const batch = await client.createBatch();
        batch.tryAdd({ body: randomString(20) });
        await client.sendBatch(batch);
        await client.close();
        res.send("Message sent to Azure Event Hubs.");
    } catch (err) {
        res.status(500).send(`Error publishing to Event Hubs: ${err}`);
    }
});

app.get("/", async (_req, res) => {
    try {
        const funcAppUrl =
            process.env["services__funcapp__http__0"] ||
            "http://localhost:7071";
        const response = await fetch(`${funcAppUrl}/api/httpTrigger?name=Aspire`);
        const text = await response.text();
        res.send(text);
    } catch (err) {
        res.status(500).send(`Error calling function: ${err}`);
    }
});

app.listen(port, () => {
    console.log(`API service listening on port ${port}`);
});

import { app, InvocationContext, output } from "@azure/functions";
import {
    backgroundWorkQueueConnection,
    backgroundWorkQueueName,
    type BackgroundWorkItem
} from "../backgroundWork";

const sampleBlobOutput = output.storageBlob({
    path: "samples/{rand-guid}.txt",
    connection: "blobs"
});

export async function backgroundWorkQueueTrigger(message: unknown, context: InvocationContext): Promise<void> {
    const workItem = getBackgroundWorkItem(message);
    const content = `Hello, ${workItem.name}! Processed from a storage queue-triggered background function. Requested at ${workItem.requestedAt}.`;

    context.extraOutputs.set(sampleBlobOutput, content);

    context.log("Background queue work wrote a sample blob via output binding.");
}

app.storageQueue("backgroundWorkQueueTrigger", {
    queueName: backgroundWorkQueueName,
    connection: backgroundWorkQueueConnection,
    extraOutputs: [sampleBlobOutput],
    handler: backgroundWorkQueueTrigger
});

function getBackgroundWorkItem(message: unknown): BackgroundWorkItem {
    // enqueueBackgroundWork writes queue messages as:
    //   {"name":"Aspire","requestedAt":"2026-06-07T21:43:00.000Z"}
    // The Functions host can pass a decoded JSON object or the raw queue string
    // depending on binding metadata, so accept both shapes.
    const raw = typeof message === "string" ? tryParseJson(message) : message;
    const candidate = raw && typeof raw === "object" ? raw as Partial<BackgroundWorkItem> : {};

    return {
        name: typeof candidate.name === "string" && candidate.name.trim() ? candidate.name : "Aspire",
        requestedAt: typeof candidate.requestedAt === "string" && candidate.requestedAt.trim() ? candidate.requestedAt : "unknown"
    };
}

function tryParseJson(value: string): unknown {
    try {
        return JSON.parse(value);
    } catch {
        return value;
    }
}

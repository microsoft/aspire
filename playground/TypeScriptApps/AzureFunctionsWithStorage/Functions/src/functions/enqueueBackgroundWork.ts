import { app, HttpRequest, HttpResponseInit, InvocationContext, output } from "@azure/functions";
import {
    backgroundWorkQueueConnection,
    backgroundWorkQueueName,
    type BackgroundWorkItem
} from "../backgroundWork";

const backgroundWorkQueueOutput = output.storageQueue({
    queueName: backgroundWorkQueueName,
    connection: backgroundWorkQueueConnection
});

export async function enqueueBackgroundWork(request: HttpRequest, context: InvocationContext): Promise<HttpResponseInit> {
    context.log("HTTP trigger enqueueing background work.");

    const name = request.query.get("name") || (await request.text()) || "Aspire";
    const workItem: BackgroundWorkItem = {
        name,
        requestedAt: new Date().toISOString()
    };

    context.extraOutputs.set(backgroundWorkQueueOutput, JSON.stringify(workItem));

    return {
        status: 202,
        jsonBody: {
            message: `Queued background work for ${name}.`,
            queueName: backgroundWorkQueueName,
            workItem
        }
    };
}

app.http("enqueueBackgroundWork", {
    methods: ["GET", "POST"],
    authLevel: "anonymous",
    extraOutputs: [backgroundWorkQueueOutput],
    handler: enqueueBackgroundWork
});

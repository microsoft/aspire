import { app, InvocationContext } from "@azure/functions";

export async function queueTrigger(queueItem: unknown, context: InvocationContext): Promise<void> {
    context.log("Queue trigger function processed:", queueItem);
}

app.storageQueue("queueTrigger", {
    queueName: "queue",
    connection: "queue",
    handler: queueTrigger,
});

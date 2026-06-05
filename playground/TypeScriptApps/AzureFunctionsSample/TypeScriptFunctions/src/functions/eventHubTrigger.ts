import { app, InvocationContext } from "@azure/functions";

export async function eventHubTrigger(messages: unknown[], context: InvocationContext): Promise<void> {
    context.log(`EventHub trigger function processed ${messages.length} messages`);
    for (const message of messages) {
        context.log("EventHub message:", message);
    }
}

app.eventHub("eventHubTrigger", {
    connection: "myhub",
    eventHubName: "myhub",
    cardinality: "many",
    handler: eventHubTrigger,
});

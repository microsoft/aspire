import { createBuilder, AzureWebPubSubRole } from './.modules/aspire.js';
import { refExpr } from './.modules/base.js';

const builder = await createBuilder();

// addAzureWebPubSub — factory method
const webpubsub = await builder.addAzureWebPubSub("webpubsub");

// addHub — adds a hub to the Web PubSub resource (with optional hubName)
const hub = await webpubsub.addHub("myhub");
const hubWithName = await webpubsub.addHub("hub2", { hubName: "customhub" });

// addEventHandler — adds an event handler to a hub
await hub.addEventHandler(refExpr`https://example.com/handler`);
await hub.addEventHandler(refExpr`https://example.com/handler2`, {
    userEventPattern: "event1",
    systemEvents: ["connect", "connected"],
});

// withRoleAssignments — assigns roles on a container resource
const container = await builder.addContainer("mycontainer", "mcr.microsoft.com/dotnet/samples:aspnetapp");
await container.withRoleAssignments(webpubsub, [
    AzureWebPubSubRole.WebPubSubServiceOwner,
    AzureWebPubSubRole.WebPubSubServiceReader,
    AzureWebPubSubRole.WebPubSubContributor,
]);

// withRoleAssignments — also available directly on AzureWebPubSubResource builder
await webpubsub.withRoleAssignments(webpubsub, [AzureWebPubSubRole.WebPubSubServiceReader]);

// withReference — generic, works via IResourceWithConnectionString
await container.withReference(webpubsub);
await container.withReference(hub);

await builder.build().run();

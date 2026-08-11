import { createBuilder, AzureWebPubSubRole } from './.aspire/modules/aspire.mjs';
import { refExpr } from './.aspire/modules/base.mjs';

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

// withWebPubSubRoleAssignments — assigns roles on a container resource
const container = await builder.addContainer("mycontainer", "mcr.microsoft.com/dotnet/samples:aspnetapp");
await container.withWebPubSubRoleAssignments(webpubsub, [
    AzureWebPubSubRole.WebPubSubServiceOwner,
    AzureWebPubSubRole.WebPubSubServiceReader,
    AzureWebPubSubRole.WebPubSubContributor,
]);

// withWebPubSubRoleAssignments — also available directly on AzureWebPubSubResource builder
await webpubsub.withWebPubSubRoleAssignments(webpubsub, [AzureWebPubSubRole.WebPubSubServiceReader]);

// withReference — generic, works via IResourceWithConnectionString
await container.withReference(webpubsub);
await container.withReference(hub);

await builder.build().run();

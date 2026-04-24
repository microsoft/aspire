import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // addAzureWebPubSub - factory method
        var webpubsub = builder.addAzureWebPubSub("webpubsub");
        // addHub - adds a hub to the Web PubSub resource (with optional hubName)
        var hub = webpubsub.addHub("myhub");
        var hubWithName = webpubsub.addHub("hub2", "customhub");
        // addEventHandler - adds an event handler to a hub
        hub.addEventHandler(ReferenceExpression.refExpr("https://example.com/handler"));
        hub.addEventHandler(ReferenceExpression.refExpr("https://example.com/handler2"), new AddEventHandlerOptions().userEventPattern("event1").systemEvents(new String[] { "connect", "connected" }));
        // withRoleAssignments - assigns roles on a container resource
        var container = builder.addContainer("mycontainer", "mcr.microsoft.com/dotnet/samples:aspnetapp");
        container.withRoleAssignments(webpubsub, new AzureWebPubSubRole[] { AzureWebPubSubRole.WEB_PUB_SUB_SERVICE_OWNER, AzureWebPubSubRole.WEB_PUB_SUB_SERVICE_READER, AzureWebPubSubRole.WEB_PUB_SUB_CONTRIBUTOR });
        // withRoleAssignments - also available directly on AzureWebPubSubResource builder
        webpubsub.withRoleAssignments(webpubsub, new AzureWebPubSubRole[] { AzureWebPubSubRole.WEB_PUB_SUB_SERVICE_READER });
        // withReference - generic, works via IResourceWithConnectionString
        container.withReference(webpubsub, new WithReferenceOptions());
        container.withReference(hub, new WithReferenceOptions());
        builder.build().run();
    }

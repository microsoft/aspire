var builder = DistributedApplication.CreateBuilder(args);

var wps = builder.AddAzureWebPubSub("wps1");
var chat = wps.AddHub("ChatForAspire");
var notification = wps.AddHub("NotificationForAspire");
var web = builder.AddProject<Projects.WebPubSubWeb>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(chat)
    .WithReference(notification);

// Now only works in production since localhost is not accessible from Azure
if (builder.ExecutionContext.IsPublishMode)
{
    wps.AddHub("ChatForAspire").AddEventHandler($"{web.GetEndpoint("https")}/eventhandler/", systemEvents: ["connected"]);
}

builder.Build().Run();

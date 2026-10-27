var builder = DistributedApplication.CreateBuilder(args);

var wps = builder.AddAzureWebPubSub("wps1").RunAsEmulator();
var chat = wps.AddHub("ChatForAspire");
var notification = wps.AddHub("NotificationForAspire");
var web = builder.AddDotnetProject("webfrontend", @"..\WebPubSubWeb\WebPubSubWeb.csproj", options => options.LaunchProfileName = "https")
    .WithExternalHttpEndpoints()
    .WithReference(chat)
    .WithReference(notification)
    .WaitFor(wps);

var callbackEndpoint = web.GetEndpoint("https");
chat.AddEventHandler($"{callbackEndpoint}/eventhandler/{chat.Resource.HubName}", systemEvents: ["connected"]);

builder.Build().Run();

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Configure Azure SignalR in default mode
var defaultSignalr = builder.AddAzureSignalR("signalrDefault");

builder.AddProject<Projects.SignalRWeb>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(defaultSignalr);

// Configure Azure SignalR in serverless mode
var serverlessSignalr = builder
    .AddAzureSignalR("signalrServerless", AzureSignalRServiceMode.Serverless)
    .RunAsEmulator();

builder.AddProject<Projects.SignalRServerlessWeb>("webserverless")
    .WithExternalHttpEndpoints()
    .WithReference(serverlessSignalr)
    .WaitFor(serverlessSignalr);

builder.Build().Run();

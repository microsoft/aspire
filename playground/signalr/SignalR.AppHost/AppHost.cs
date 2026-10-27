using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Configure Azure SignalR in default mode
var defaultSignalr = builder.AddAzureSignalR("signalrDefault");

builder.AddDotnetProject("webfrontend", @"..\SignalRWeb\SignalRWeb.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(defaultSignalr);

// Configure Azure SignalR in serverless mode
var serverlessSignalr = builder
    .AddAzureSignalR("signalrServerless", AzureSignalRServiceMode.Serverless)
    .RunAsEmulator();

builder.AddDotnetProject("webserverless", @"..\SignalRServerlessWeb\SignalRServerlessWeb.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(serverlessSignalr)
    .WaitFor(serverlessSignalr);

builder.Build().Run();

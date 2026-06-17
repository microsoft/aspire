var builder = DistributedApplication.CreateBuilder(args);

// required for the event processor client which will use the connectionName to get the connectionString.
var blob = builder.AddAzureStorage("ehstorage")
    .RunAsEmulator()
    .AddBlobs("checkpoints");

var eventHub = builder.AddAzureEventHubs("eventhubns")
    .RunAsEmulator()
    .AddHub("eventhubOne", "eventhub");

builder.AddProject<Projects.EventHubsConsumer>("consumer")
    .WithReference(eventHub).WaitFor(eventHub)
    .WithReference(blob);

builder.AddProject<Projects.EventHubsApi>("api")
    .WithExternalHttpEndpoints()
    .WithReference(eventHub).WaitFor(eventHub);

builder.Build().Run();

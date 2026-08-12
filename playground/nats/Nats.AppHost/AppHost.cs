var builder = DistributedApplication.CreateBuilder(args);

var nats = builder.AddNats("nats")
    .WithJetStream();

builder.AddProject<Projects.Nats_ApiService>("api")
    .WithExternalHttpEndpoints()
    .WithReference(nats)
    .WaitFor(nats);

builder.AddProject<Projects.Nats_Backend>("backend")
    .WithReference(nats)
    .WaitFor(nats);

builder.Build().Run();

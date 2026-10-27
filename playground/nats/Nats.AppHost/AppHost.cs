var builder = DistributedApplication.CreateBuilder(args);

var nats = builder.AddNats("nats")
    .WithJetStream();

builder.AddDotnetProject("api", @"..\Nats.ApiService\Nats.ApiService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(nats)
    .WaitFor(nats);

builder.AddDotnetProject("backend", @"..\Nats.Backend\Nats.Backend.csproj")
    .WithReference(nats)
    .WaitFor(nats);

builder.Build().Run();

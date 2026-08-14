var builder = DistributedApplication.CreateBuilder(args);

builder.AddRustApp("app", "../app")
    .WithHttpEndpoint(env: "PORT")
    .WithHttpHealthCheck("/health");

builder.Build().Run();

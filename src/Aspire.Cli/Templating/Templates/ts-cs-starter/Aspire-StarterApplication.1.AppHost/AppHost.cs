var builder = DistributedApplication.CreateBuilder(args);

{{#useRedisCache}}
var cache = builder.AddRedis("cache");

{{/useRedisCache}}
var server = builder.AddProject<Projects.Aspire_StarterApplication._1_Server>("server")
{{#useRedisCache}}
    .WithReference(cache)
    .WaitFor(cache)
{{/useRedisCache}}
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();

var builder = DistributedApplication.CreateBuilder(args);

#if UseRedisCache
var cache = builder.AddRedis("cache");

#endif
var server = builder.AddDotnetProject("server", "../Aspire-StarterApplication.1.Server/Aspire-StarterApplication.1.Server.csproj")
#if UseRedisCache
    .WithReference(cache)
    .WaitFor(cache)
#endif
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();

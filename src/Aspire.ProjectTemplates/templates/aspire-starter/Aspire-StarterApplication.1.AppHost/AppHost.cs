var builder = DistributedApplication.CreateBuilder(args);

#if UseRedisCache
var cache = builder.AddRedis("cache");

#endif
var apiService = builder.AddDotnetProject("apiservice", "../Aspire-StarterApplication.1.ApiService/")
    .WithHttpHealthCheck("/health");

builder.AddDotnetProject("webfrontend", "../Aspire-StarterApplication.1.Web/")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
#if UseRedisCache
    .WithReference(cache)
    .WaitFor(cache)
#endif
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

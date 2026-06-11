var builder = DistributedApplication.CreateBuilder(args);

#if UseRedisCache
var cache = builder.AddRedis("cache");

#endif
var apiService = builder.AddProject<Projects.GeneratedClassNamePrefix_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.GeneratedClassNamePrefix_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
#if UseRedisCache
    .WithReference(cache)
    .WaitFor(cache)
#endif
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

#:project .aspire/modules/Aspire.csproj

var builder = DistributedApplication.CreateBuilder(args);

builder.AddRedis("cache");

builder.Build().Run();

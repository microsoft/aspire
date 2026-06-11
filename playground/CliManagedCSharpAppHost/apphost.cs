#:project .aspire/modules/Aspire.csproj

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddRedis("cache");

builder.Build().Run();

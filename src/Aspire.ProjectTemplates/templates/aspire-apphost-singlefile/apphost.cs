#:package Aspire.Hosting.AppHost@!!REPLACE_WITH_LATEST_VERSION!!
#:package Aspire.Hosting.Dotnet@!!REPLACE_WITH_LATEST_VERSION!!
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();

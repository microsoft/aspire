#:package Aspire.Hosting.AppHost@{{aspireVersion}}
#:package Aspire.Hosting.Dotnet@{{aspireVersion}}
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();

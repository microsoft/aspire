#:sdk Aspire.AppHost.Sdk@{{aspireVersion}}
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();

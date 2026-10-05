#:sdk Aspire.AppHost.Sdk@!!REPLACE_WITH_LATEST_VERSION!!
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();

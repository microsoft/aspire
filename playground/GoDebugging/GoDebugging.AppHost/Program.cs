#pragma warning disable ASPIREGO001

var builder = DistributedApplication.CreateBuilder(args);

builder.AddGoApp("api", "../api", buildTags: ["playground"])
       .WithAppArgs("--message", "hello-from-aspire")
       .WithHttpEndpoint(env: "PORT")
       .WithExternalHttpEndpoints();

builder.Build().Run();

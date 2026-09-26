var builder = DistributedApplication.CreateBuilder(args);

builder.AddRustApp("app", "../app")
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithHttpEndpoint(env: "PORT")
    .WithHttpHealthCheck("/health");

builder.Build().Run();

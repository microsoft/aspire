var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("docker-compose");

builder.AddCMakeApp("cpp-api", "../cpp-api", targetName: "cpp-api")
       .WithVcpkg()
       .WithCMakeInstall()
       .WithHttpEndpoint(env: "PORT")
       .WithExternalHttpEndpoints();

builder.Build().Run();

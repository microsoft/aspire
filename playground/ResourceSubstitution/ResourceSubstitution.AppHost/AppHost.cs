#pragma warning disable ASPIRECSHARPAPPS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var builder = DistributedApplication.CreateBuilder(args);

var projectPath = new Projects.ResourceSubstitution_Api().ProjectPath;

// Normal project resource
// Overriding ports from launch settings to avoid conflicting with the substituted resource
builder.AddCSharpApp("project", projectPath)
    .WithEndpoint("http", x => x.Port = null)
    .WithEndpoint("https", x => x.Port = null)
    .WithHttpHealthCheck();

//A regular container resource
builder.AddContainer("container", "aspire/resourcesubstitution.apphost/container-from-project")
    .WithImageTag("aspire-image-build")
    .WithHttpEndpoint(targetPort: 8080, env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpsEndpoint(targetPort: 8443, env: "ASPNETCORE_HTTPS_PORTS")
    .WithHttpHealthCheck()
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = ctx.CertificatePath;
        ctx.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] = ctx.KeyPath;
        if (ctx.Password is not null)
        {
            ctx.EnvironmentVariables["Kestrel__Certificates__Default__Password"] = ctx.Password;
        }

        return Task.CompletedTask;
    });

// A container resource, we change to running a project
// Including picking up ports from launch settings
builder.AddContainer("project-from-container", "doesnt-matter")
    .RunAsProject(projectPath)
    .WithHttpHealthCheck();

// A project resource that we run as a container
// Overriding ports as the launch settings port so as to not conflict with `project-from-container`
builder.AddProject<Projects.ResourceSubstitution_Api>("container-from-project")
    .WithEndpoint("http", x => x.Port = null)
    .WithEndpoint("https", x => x.Port = null)
    .WithHttpHealthCheck()
    .RunAsContainer();

// A project resource that we run as a .NET tool
builder.AddProject<Projects.ResourceSubstitution_Api>("tool-from-project")
    .RunAsTool();

builder.Build().Run();
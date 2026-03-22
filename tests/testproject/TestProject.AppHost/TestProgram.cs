// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001 // WithoutHttpsCertificate is experimental

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.TestProject;
using Microsoft.Extensions.DependencyInjection;

public class TestProgram : IDisposable
{
    private const string AspireTestContainerRegistry = "netaspireci.azurecr.io";

    private TestProgram(
        string testName,
        string[] args,
        string assemblyName,
        bool disableDashboard,
        bool includeIntegrationServices,
        bool allowUnsecuredTransport,
        bool randomizePorts,
        bool? trustDeveloperCertificate = null)
    {
        TestResourceNames resourcesToSkip = TestResourceNames.None;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--skip-resources", StringComparison.InvariantCultureIgnoreCase))
            {
                if (args.Length > i + 1)
                {
                    resourcesToSkip = TestResourceNamesExtensions.Parse(args[i + 1].Split(','));
                    break;
                }
                else
                {
                    throw new ArgumentException("Missing argument to --skip-resources option.");
                }
            }
        }
        if (resourcesToSkip.HasFlag(TestResourceNames.dashboard))
        {
            disableDashboard = true;
        }

        var options = new DistributedApplicationOptions()
        {
            Args = args,
            DisableDashboard = disableDashboard,
            AssemblyName = assemblyName,
            AllowUnsecuredTransport = allowUnsecuredTransport,
            EnableResourceLogging = true
        };

        if (trustDeveloperCertificate.HasValue)
        {
            options.TrustDeveloperCertificate = trustDeveloperCertificate.Value;
        }

        var builder = DistributedApplication.CreateBuilder(options);

        builder.Configuration["DcpPublisher:ResourceNameSuffix"] = $"{Random.Shared.Next():x}";
        builder.Configuration["DcpPublisher:RandomizePorts"] = randomizePorts.ToString(CultureInfo.InvariantCulture);
        builder.Configuration["DcpPublisher:WaitForResourceCleanup"] = "true";
        builder.Configuration["DcpPublisher:LogFileNameSuffix"] = testName;

        AppBuilder = builder;

        string testPrefix = string.IsNullOrEmpty(testName) ? "" : $"{testName}-";
        var serviceAPath = Path.Combine(Projects.TestProject_AppHost.ProjectPath, @"..\TestProject.ServiceA\TestProject.ServiceA.csproj");

        ServiceABuilder = AppBuilder.AddProject($"{testPrefix}servicea", serviceAPath, launchProfileName: "http");
        ServiceBBuilder = AppBuilder.AddProject<Projects.ServiceB>($"{testPrefix}serviceb", launchProfileName: "http");
        ServiceCBuilder = AppBuilder.AddProject<Projects.ServiceC>($"{testPrefix}servicec", launchProfileName: "http");
        WorkerABuilder = AppBuilder.AddProject<Projects.WorkerA>($"{testPrefix}workera");

        if (includeIntegrationServices)
        {
            IntegrationServiceABuilder = AppBuilder.AddProject<Projects.IntegrationServiceA>($"{testPrefix}integrationservicea");
            IntegrationServiceABuilder = IntegrationServiceABuilder.WithEnvironment("SKIP_RESOURCES", string.Join(',', resourcesToSkip));

            if (!resourcesToSkip.HasFlag(TestResourceNames.redis))
            {
                var redis = AppBuilder.AddRedis($"{testPrefix}redis")
                    .WithImageRegistry(AspireTestContainerRegistry)
                    .WithoutHttpsCertificate();
                IntegrationServiceABuilder = IntegrationServiceABuilder.WithReference(redis);
            }
            if (!resourcesToSkip.HasFlag(TestResourceNames.postgres) || !resourcesToSkip.HasFlag(TestResourceNames.efnpgsql))
            {
                var postgresDbName = "postgresdb";
                var postgres = AppBuilder.AddPostgres($"{testPrefix}postgres")
                    .WithImageRegistry(AspireTestContainerRegistry)
                    .WithEnvironment("POSTGRES_DB", postgresDbName)
                    .AddDatabase(postgresDbName);
                IntegrationServiceABuilder = IntegrationServiceABuilder.WithReference(postgres);
            }
        }

        AppBuilder.Services.TryAddEventingSubscriber<EndPointWriterHook>();
        AppBuilder.Services.AddHttpClient();
    }

    public static TestProgram Create<T>(
       string[]? args = null,
       bool includeIntegrationServices = false,
       bool disableDashboard = true,
       bool allowUnsecuredTransport = true,
       bool randomizePorts = true,
       bool? trustDeveloperCertificate = null)
    {
        return Create<T>("", args, includeIntegrationServices, disableDashboard, allowUnsecuredTransport, randomizePorts, trustDeveloperCertificate);
    }

    public static TestProgram Create<T>(
        string testName,
        string[]? args = null,
        bool includeIntegrationServices = false,
        bool disableDashboard = true,
        bool allowUnsecuredTransport = true,
        bool randomizePorts = true,
        bool? trustDeveloperCertificate = null)
    {
        return new TestProgram(
            testName,
            args ?? [],
            assemblyName: typeof(T).Assembly.FullName!,
            disableDashboard: disableDashboard,
            includeIntegrationServices: includeIntegrationServices,
            allowUnsecuredTransport: allowUnsecuredTransport,
            randomizePorts: randomizePorts,
            trustDeveloperCertificate: trustDeveloperCertificate);
    }

    public IDistributedApplicationBuilder AppBuilder { get; private set; }
    public IResourceBuilder<ProjectResource> ServiceABuilder { get; private set; }
    public IResourceBuilder<ProjectResource> ServiceBBuilder { get; private set; }
    public IResourceBuilder<ProjectResource> ServiceCBuilder { get; private set; }
    public IResourceBuilder<ProjectResource> WorkerABuilder { get; private set; }
    public IResourceBuilder<ProjectResource>? IntegrationServiceABuilder { get; private set; }
    public DistributedApplication? App { get; private set; }

    public List<IResourceBuilder<ProjectResource>> ServiceProjectBuilders => [ServiceABuilder, ServiceBBuilder, ServiceCBuilder];

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var app = Build();
        await app.RunAsync(cancellationToken);
    }

    public DistributedApplication Build()
    {
        return App ??= AppBuilder.Build();
    }

    public void Run()
    {
        var app = Build();
        app.Run();
    }

    public void Dispose() => App?.Dispose();

    /// <summary>
    /// Writes the allocated endpoints to the console in JSON format.
    /// This allows for easier consumption by the external test process.
    /// </summary>
    private sealed class EndPointWriterHook : IDistributedApplicationEventingSubscriber
    {
        public async Task OnAfterResourcesCreated(AfterResourcesCreatedEvent @event, CancellationToken cancellationToken)
        {
            var root = new JsonObject();
            foreach (var project in @event.Model.Resources.OfType<ProjectResource>())
            {
                var projectJson = new JsonObject();
                root[project.Name] = projectJson;

                var endpointsJsonArray = new JsonArray();
                projectJson["Endpoints"] = endpointsJsonArray;

                foreach (var endpoint in project.Annotations.OfType<EndpointAnnotation>())
                {
                    var allocatedEndpoint = endpoint.AllocatedEndpoint;
                    if (allocatedEndpoint is null)
                    {
                        continue;
                    }

                    var endpointJsonObject = new JsonObject
                    {
                        ["Name"] = endpoint.Name,
                        ["Uri"] = allocatedEndpoint.UriString
                    };
                    endpointsJsonArray.Add(endpointJsonObject);
                }
            }

            // write the whole json in a single line so it's easier to parse by the external process
            await Console.Out.WriteLineAsync("$ENDPOINTS: " + JsonSerializer.Serialize(root, JsonSerializerOptions.Default));
        }

        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            // We can assume endpoints are allocated before project resources are created
            eventing.Subscribe<AfterResourcesCreatedEvent>(OnAfterResourcesCreated);
            return Task.CompletedTask;
        }
    }
}


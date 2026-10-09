// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using SamplesIntegrationTests.Infrastructure;
using Xunit.Sdk;

namespace SamplesIntegrationTests.Infrastructure;

public static partial class DistributedApplicationExtensions
{
    /// <summary>
    /// Ensures all parameters in the application configuration have values set.
    /// </summary>
    public static TBuilder WithRandomParameterValues<TBuilder>(this TBuilder builder)
        where TBuilder : IDistributedApplicationTestingBuilder
    {
        var parameters = builder.Resources.OfType<ParameterResource>().Where(p => !p.IsConnectionString).ToList();
        foreach (var parameter in parameters)
        {
            builder.Configuration[$"Parameters:{parameter.Name}"] = parameter.Secret
                ? PasswordGenerator.Generate(16, true, true, true, false, 1, 1, 1, 0)
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        }

        return builder;
    }

    /// <summary>
    /// Replaces all named volumes with anonymous volumes so they're isolated across test runs and from the volume the app uses during development.
    /// </summary>
    /// <remarks>
    /// Note that if multiple resources share a volume, the volume will instead be given a random name so that it's still shared across those resources in the test run.
    /// </remarks>
    public static TBuilder WithRandomVolumeNames<TBuilder>(this TBuilder builder)
        where TBuilder : IDistributedApplicationTestingBuilder
    {
        // Named volumes that aren't shared across resources should be replaced with anonymous volumes.
        // Named volumes shared by mulitple resources need to have their name randomized but kept shared across those resources.

        // Find all shared volumes and make a map of their original name to a new randomized name
        var allResourceNamedVolumes = builder.Resources.SelectMany(r => r.Annotations
            .OfType<ContainerMountAnnotation>()
            .Where(m => m.Type == ContainerMountType.Volume && !string.IsNullOrEmpty(m.Source))
            .Select(m => (Resource: r, Volume: m)))
            .ToList();
        var seenVolumes = new HashSet<string>();
        var renamedVolumes = new Dictionary<string, string>();
        foreach (var resourceVolume in allResourceNamedVolumes)
        {
            var name = resourceVolume.Volume.Source!;
            if (!seenVolumes.Add(name) && !renamedVolumes.ContainsKey(name))
            {
                renamedVolumes[name] = $"{name}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            }
        }

        // Replace all named volumes with randomly named or anonymous volumes
        foreach (var resourceVolume in allResourceNamedVolumes)
        {
            var resource = resourceVolume.Resource;
            var volume = resourceVolume.Volume;
            var newName = renamedVolumes.TryGetValue(volume.Source!, out var randomName) ? randomName : null;
            var newMount = new ContainerMountAnnotation(newName, volume.Target, ContainerMountType.Volume, volume.IsReadOnly);
            resource.Annotations.Remove(volume);
            resource.Annotations.Add(newMount);
        }

        return builder;
    }

    /// <summary>
    /// Waits for the specified resource to reach the specified state.
    /// </summary>
    public static Task WaitForResource(this DistributedApplication app, string resourceName, string? targetState = null, CancellationToken cancellationToken = default)
    {
        targetState ??= KnownResourceStates.Running;

        return app.ResourceNotifications.WaitForResourceAsync(resourceName, targetState, cancellationToken);
    }

    /// <summary>
    /// Waits for all resources in the application to reach one of the specified states.
    /// </summary>
    /// <remarks>
    /// If <paramref name="targetStates"/> is null, the default states are <see cref="KnownResourceStates.Running"/> and <see cref="KnownResourceStates.Hidden"/>.
    /// </remarks>
    public static Task WaitForResources(this DistributedApplication app, IEnumerable<string>? targetStates = null, CancellationToken cancellationToken = default)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        targetStates ??= [KnownResourceStates.Running, KnownResourceStates.Hidden];
#pragma warning restore CS0618 // Type or member is obsolete

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        return Task.WhenAll(applicationModel.Resources.Select(r => app.ResourceNotifications.WaitForResourceAsync(r.Name, r => targetStates?.Contains(r.Snapshot.State?.Text, StringComparer.OrdinalIgnoreCase) is true || r.Snapshot.IsHidden, cancellationToken)));
    }

    /// <summary>
    /// Gets the app host and resource logs from the application.
    /// </summary>
    public static (IReadOnlyList<FakeLogRecord> AppHostLogs, IReadOnlyList<FakeLogRecord> ResourceLogs) GetLogs(this DistributedApplication app)
    {
        var environment = app.Services.GetRequiredService<IHostEnvironment>();
        var logCollector = app.Services.GetFakeLogCollector();
        var logs = logCollector.GetSnapshot();
        var appHostLogs = logs.Where(l => l.Category?.StartsWith($"{environment.ApplicationName}.Resources") == false).ToList();
        var resourceLogs = logs.Where(l => l.Category?.StartsWith($"{environment.ApplicationName}.Resources") == true).ToList();

        return (appHostLogs, resourceLogs);
    }

    /// <summary>
    /// Asserts that no errors were logged by the application or any of its resources.
    /// </summary>
    /// <remarks>
    /// Some resource types are excluded from this check because they tend to write to stderr for various non-error reasons.
    /// </remarks>
    /// <param name="app"></param>
    public static void EnsureNoErrorsLogged(this DistributedApplication app)
    {
        var environment = app.Services.GetRequiredService<IHostEnvironment>();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var assertableResourceLogNames = applicationModel.Resources.Where(ShouldAssertErrorsForResource).Select(r => $"{environment.ApplicationName}.Resources.{r.Name}").ToList();

        var (appHostlogs, resourceLogs) = app.GetLogs();

        // Ignoring log entries from DefaultHealthCheckService for now. Once we have dashboard integration for health checks
        // we'll filter out these log messages from the apphost completely but its useful for debugging for now.
        AssertDoesNotContain(appHostlogs, log => log.Level >= LogLevel.Error && log.Category != "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService");
        AssertDoesNotContain(resourceLogs, log => log.Category is { Length: > 0 } category && assertableResourceLogNames.Contains(category) && log.Level >= LogLevel.Error);

        static bool ShouldAssertErrorsForResource(IResource resource)
        {
            return resource
                is
                    // Container resources tend to write to stderr for various reasons so only assert projects and executables
                    (ProjectResource or ExecutableResource)
                    // Node resources tend to have npm modules that write to stderr so ignore them
                    and not NodeAppResource;
        }

        static void AssertDoesNotContain(IReadOnlyList<FakeLogRecord> logs, Func<FakeLogRecord, bool> predicate)
        {
            foreach (var log in logs)
            {
                if (predicate(log))
                {
                    throw new XunitException($"Error found in the logs: {log}");
                }
            }
        }
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured to communicate with the specified resource.
    /// </summary>
    public static HttpClient CreateHttpClient(this DistributedApplication app, string resourceName, bool useHttpClientFactory)
        => app.CreateHttpClient(resourceName, null, useHttpClientFactory);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured to communicate with the specified resource.
    /// </summary>
    public static HttpClient CreateHttpClient(this DistributedApplication app, string resourceName, string? endpointName, bool useHttpClientFactory)
    {
        if (useHttpClientFactory)
        {
            return app.CreateHttpClient(resourceName, endpointName);
        }

        // Don't use the HttpClientFactory to create the HttpClient so, e.g., no resilience policies are applied
        var httpClient = new HttpClient
        {
            BaseAddress = app.GetEndpoint(resourceName, endpointName)
        };

        return httpClient;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured to communicate with the specified resource with custom configuration.
    /// </summary>
    public static HttpClient CreateHttpClient(this DistributedApplication app, string resourceName, string? endpointName, Action<IHttpClientBuilder> configure)
    {
        var services = new ServiceCollection()
            .AddHttpClient()
            .ConfigureHttpClientDefaults(configure)
            .BuildServiceProvider();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = app.GetEndpoint(resourceName, endpointName);

        return httpClient;
    }

    /// <summary>
    /// Attempts to apply EF migrations for the specified project by sending a request to the migrations endpoint <c>/ApplyDatabaseMigrations</c>.
    /// </summary>
    public static async Task<bool> TryApplyEfMigrationsAsync(this DistributedApplication app, ProjectResource project)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(TryApplyEfMigrationsAsync));
        var projectName = project.GetName();

        // First check if the project has a migration endpoint, if it doesn't it will respond with a 404
        logger.LogInformation("Checking if project '{ProjectName}' has a migration endpoint", projectName);
        using (var checkHttpClient = app.CreateHttpClient(project.Name))
        {
            using var emptyDbContextContent = new FormUrlEncodedContent([new("context", "")]);
            using var checkResponse = await checkHttpClient.PostAsync("/ApplyDatabaseMigrations", emptyDbContextContent);
            if (checkResponse.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Project '{ProjectName}' does not have a migration endpoint", projectName);
                return false;
            }
        }

        logger.LogInformation("Attempting to apply EF migrations for project '{ProjectName}'", projectName);

        // Load the project assembly and find all DbContext types
        var projectDirectory = Path.GetDirectoryName(project.GetProjectMetadata().ProjectPath) ?? throw new UnreachableException();
#if DEBUG
        var configuration = "Debug";
#else
        var configuration = "Release";
#endif
        var projectAssemblyPath = Path.Combine(projectDirectory, "bin", configuration, "net10.0", $"{projectName}.dll");
        var projectAssembly = Assembly.LoadFrom(projectAssemblyPath);
        var dbContextTypes = projectAssembly.GetTypes().Where(DerivesFromDbContext);

        logger.LogInformation("Found {DbContextCount} DbContext types in project '{ProjectName}'", dbContextTypes.Count(), projectName);

        // Call the migration endpoint for each DbContext type
        var migrationsApplied = false;
        using var applyMigrationsHttpClient = app.CreateHttpClient(project.Name, useHttpClientFactory: false);
        applyMigrationsHttpClient.Timeout = TimeSpan.FromSeconds(240);
        foreach (var dbContextType in dbContextTypes)
        {
            logger.LogInformation("Applying migrations for DbContext '{DbContextType}' in project '{ProjectName}'", dbContextType.FullName, projectName);
            using var content = new FormUrlEncodedContent([new("context", dbContextType.AssemblyQualifiedName)]);
            using var response = await applyMigrationsHttpClient.PostAsync("/ApplyDatabaseMigrations", content);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                migrationsApplied = true;
                logger.LogInformation("Migrations applied for DbContext '{DbContextType}' in project '{ProjectName}'", dbContextType.FullName, projectName);
            }
        }

        return migrationsApplied;
    }

    private static bool DerivesFromDbContext(Type type)
    {
        var baseType = type.BaseType;

        while (baseType is not null)
        {
            if (baseType.FullName == "Microsoft.EntityFrameworkCore.DbContext" && baseType.Assembly.GetName().Name == "Microsoft.EntityFrameworkCore")
            {
                return true;
            }

            baseType = baseType.BaseType;
        }

        return false;
    }
}

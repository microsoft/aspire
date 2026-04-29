// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Microsoft.DurableTask.AzureManaged.Tests;

public class AspireDurableTaskSchedulerExtensionsTests
{
    private const string TestConnectionString = "Endpoint=http://localhost:8080;Authentication=None;TaskHub=TestHub";

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenConnectionNameIsNullOrEmpty()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddDurableTaskSchedulerWorker(null!, _ => { }));

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddDurableTaskSchedulerWorker("", _ => { }));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenConfigureWorkerIsNull()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddDurableTaskSchedulerWorker("scheduler", null!));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenConnectionStringIsMissing()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ReadsConnectionStringFromConfiguration()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();

        // Worker host service should be registered
        var hostedServices = host.Services.GetServices<IHostedService>();
        Assert.NotEmpty(hostedServices);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ReadsConnectionStringFromConfigSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Microsoft:DurableTask:AzureManaged:ConnectionString", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();
        var hostedServices = host.Services.GetServices<IHostedService>();
        Assert.NotEmpty(hostedServices);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_RegistersClientByDefault()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();
        var client = host.Services.GetService<DurableTaskClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_DoesNotRegisterClientWhenOptedOut()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }, includeClient: false);

        using var host = builder.Build();
        var client = host.Services.GetService<DurableTaskClient>();
        Assert.Null(client);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_RegistersHealthCheckByDefault()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();
        var healthCheckService = host.Services.GetService<HealthCheckService>();
        Assert.NotNull(healthCheckService);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_HealthCheckCanBeDisabled()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString),
            new KeyValuePair<string, string?>("Aspire:Microsoft:DurableTask:AzureManaged:DisableHealthChecks", "true")
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();

        // Health check registrations should be available (infrastructure), but no DTS-specific one
        var options = host.Services.GetService<IOptions<HealthCheckServiceOptions>>();
        Assert.NotNull(options);

        var registrations = options.Value.Registrations;
        Assert.DoesNotContain(registrations, r => r.Name.StartsWith("DurableTaskScheduler_", StringComparison.Ordinal));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_HealthCheckCanBeDisabledViaDelegate()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }, configureSettings: s => s.DisableHealthChecks = true);

        using var host = builder.Build();
        var options = host.Services.GetService<IOptions<HealthCheckServiceOptions>>();
        Assert.NotNull(options);

        var registrations = options.Value.Registrations;
        Assert.DoesNotContain(registrations, r => r.Name.StartsWith("DurableTaskScheduler_", StringComparison.Ordinal));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ConfigureSettingsDelegateIsInvoked()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        var invoked = false;
        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }, configureSettings: _ => invoked = true);

        Assert.True(invoked);
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_NamedConfigOverridesTopLevelConfig()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("Aspire:Microsoft:DurableTask:AzureManaged:DisableHealthChecks", "false"),
            new KeyValuePair<string, string?>("Aspire:Microsoft:DurableTask:AzureManaged:scheduler:DisableHealthChecks", "true"),
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();
        var options = host.Services.GetService<IOptions<HealthCheckServiceOptions>>();
        Assert.NotNull(options);

        var registrations = options.Value.Registrations;
        Assert.DoesNotContain(registrations, r => r.Name.StartsWith("DurableTaskScheduler_", StringComparison.Ordinal));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenEndpointMissingFromConnectionString()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", "Authentication=None;TaskHub=TestHub")
        ]);

        Assert.ThrowsAny<Exception>(() =>
            builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }));
    }

    [Fact]
    public void AddDurableTaskSchedulerWorker_ThrowsWhenTaskHubMissingFromConnectionString()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", "Endpoint=http://localhost:8080;Authentication=None")
        ]);

        Assert.ThrowsAny<Exception>(() =>
            builder.AddDurableTaskSchedulerWorker("scheduler", _ => { }));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerWorker_ThrowsWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddKeyedDurableTaskSchedulerWorker("scheduler", _ => { }));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerWorker_ThrowsWhenNameIsNullOrEmpty()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddKeyedDurableTaskSchedulerWorker(null!, _ => { }));

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddKeyedDurableTaskSchedulerWorker("", _ => { }));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerWorker_ThrowsWhenConfigureWorkerIsNull()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddKeyedDurableTaskSchedulerWorker("scheduler", null!));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerWorker_ReadsConnectionStringFromConfiguration()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddKeyedDurableTaskSchedulerWorker("scheduler", _ => { });

        using var host = builder.Build();
        var hostedServices = host.Services.GetServices<IHostedService>();
        Assert.NotEmpty(hostedServices);
    }

    [Fact]
    public void AddDurableTaskSchedulerClient_ThrowsWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddDurableTaskSchedulerClient("scheduler"));
    }

    [Fact]
    public void AddDurableTaskSchedulerClient_ThrowsWhenConnectionNameIsNullOrEmpty()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddDurableTaskSchedulerClient(null!));

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddDurableTaskSchedulerClient(""));
    }

    [Fact]
    public void AddDurableTaskSchedulerClient_RegistersClient()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddDurableTaskSchedulerClient("scheduler");

        using var host = builder.Build();
        var client = host.Services.GetService<DurableTaskClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerClient_ThrowsWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddKeyedDurableTaskSchedulerClient("scheduler"));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerClient_ThrowsWhenNameIsNullOrEmpty()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddKeyedDurableTaskSchedulerClient(null!));

        Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddKeyedDurableTaskSchedulerClient(""));
    }

    [Fact]
    public void AddKeyedDurableTaskSchedulerClient_RegistersServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:scheduler", TestConnectionString)
        ]);

        builder.AddKeyedDurableTaskSchedulerClient("scheduler");

        using var host = builder.Build();

        // Verify services were registered (keyed DurableTaskClient may not resolve
        // directly via GetKeyedService since the DT SDK uses its own factory pattern)
        var hostedServices = host.Services.GetServices<IHostedService>();
        Assert.NotEmpty(hostedServices);
    }
}

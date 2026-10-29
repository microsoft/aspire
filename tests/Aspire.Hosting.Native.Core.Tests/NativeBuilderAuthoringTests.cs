// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias RedisContracts;

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.Native.Authoring;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using NativeIntegrations.Redis;
using static RedisContracts::NativeIntegrations.Redis.AppHost.RedisBuilderExtensions;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeBuilderAuthoringTests
{
    [Fact]
    public void AuthoringUsesStandardConfigurationWithArgumentsOverridingSuppliedValues()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["application:mode"] = "configured",
            ["application:retained"] = "retained",
            ["ASPIRE_NATIVE_RPC_ENDPOINT"] = "127.0.0.1:12345",
            ["ASPIRE_NATIVE_RPC_TOKEN"] = "test-token"
        });
        var builder = DistributedApplication.CreateBuilder(["--application:mode=argument"],
            new() { Configuration = configuration });
        Assert.IsAssignableFrom<IConfiguration>(builder.Configuration);
        Assert.Equal("argument", builder.Configuration.GetSection("application")["mode"]);
        Assert.Equal("retained", builder.Configuration.GetSection("application")["retained"]);
        Assert.Equal("127.0.0.1:12345", builder.Settings.Endpoint);
        Assert.Equal("test-token", builder.Configuration["ASPIRE_NATIVE_RPC_TOKEN"]);
        Assert.Equal("configured", configuration["application:mode"]);
        var overridden = DistributedApplication.CreateBuilder(["--native-endpoint=127.0.0.1:54321"],
            new() { Configuration = configuration });
        Assert.Equal("127.0.0.1:54321", overridden.Settings.Endpoint);
    }

    [Fact]
    public async Task SuppliedConfigurationAuthenticatesTheNativeApplication()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_NATIVE_RPC_ENDPOINT"] = host.Endpoint.ToString(),
            ["ASPIRE_NATIVE_RPC_TOKEN"] = host.Token
        });
        await using var application = DistributedApplication.CreateBuilder([], new() { Configuration = configuration }).Build();
        await application.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void DefaultVolumesUseStableApplicationIdentityAndPortableCrossLanguageVectors()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\native-volume-tests\shop" : "/native-volume-tests/shop";
        var options = new NativeBuilderOptions { ApplicationRoot = root };
        var first = DistributedApplication.CreateBuilder([], options);
        var cache = first.AddRedis("cache").WithDataVolume();
        var expected = OperatingSystem.IsWindows() ? "aspire-shop-9e1a5d5bed7f2e30-cache-data" : "aspire-shop-150be96b0b469209-cache-data";
        Assert.Equal(expected, first.GetDefaultDataVolumeName(cache.Resource));
        first.Build();

        var rerun = DistributedApplication.CreateBuilder([], options);
        Assert.Equal(expected, rerun.GetDefaultDataVolumeName(rerun.AddRedis("cache").WithDataVolume().Resource));
        var revision = DistributedApplication.CreateBuilder(
            [$"--native-application-root={Path.Combine(root, "..", "shop")}{Path.DirectorySeparatorChar}"]);
        Assert.Equal(expected, revision.GetDefaultDataVolumeName(revision.AddRedis("cache").WithDataVolume().Resource));

        var otherRoot = OperatingSystem.IsWindows() ? @"C:\native-volume-tests\other\shop" : "/native-volume-tests/other/shop";
        var other = DistributedApplication.CreateBuilder([], new() { ApplicationRoot = otherRoot });
        Assert.Equal(OperatingSystem.IsWindows() ? "aspire-shop-66cbb558beb5aaf5-cache-data" : "aspire-shop-7dd235e7275aa500-cache-data",
            other.GetDefaultDataVolumeName(other.AddRedis("cache").WithDataVolume().Resource));

        var named = DistributedApplication.CreateBuilder([], new() { ApplicationRoot = root, ApplicationName = "Inventory / 世界" });
        var namedVolume = named.GetDefaultDataVolumeName(named.AddRedis("cache").WithDataVolume().Resource);
        Assert.Equal(OperatingSystem.IsWindows() ? "aspire-inventory_____-9556ab04977d6d99-cache-data" : "aspire-inventory_____-76d0c05eac46aedc-cache-data",
            namedVolume);
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", namedVolume);
        var selected = DistributedApplication.CreateBuilder(
            [$"--native-application-root={root}", "--native-application-name=Inventory / 世界"],
            new() { ApplicationRoot = otherRoot, ApplicationName = "ignored" });
        Assert.Equal(namedVolume, selected.GetDefaultDataVolumeName(selected.AddRedis("cache").Resource));
        Assert.Throws<ArgumentException>(() => DistributedApplication.CreateBuilder([], new() { ApplicationRoot = "" }));
        Assert.Throws<ArgumentException>(() => DistributedApplication.CreateBuilder([], new() { ApplicationName = " " }));

        var explicitPrefix = DistributedApplication.CreateBuilder(
            ["--native-volume-prefix=User.Exact_Prefix"], new() { ApplicationRoot = otherRoot });
        Assert.Equal("User.Exact_Prefix-cache-data",
            explicitPrefix.GetDefaultDataVolumeName(explicitPrefix.AddRedis("cache").WithDataVolume().Resource));

        var defaults = DistributedApplication.CreateBuilder([]);
        var launchRoot = DistributedApplication.CreateBuilder([], new() { ApplicationRoot = Directory.GetCurrentDirectory() });
        Assert.Equal(defaults.GetDefaultDataVolumeName(defaults.AddRedis("cache").Resource),
            launchRoot.GetDefaultDataVolumeName(launchRoot.AddRedis("cache").Resource));
    }

    [Fact]
    public async Task RetainedVolumeIdentitySurvivesNativeRevisionsAndSeparatesApplicationRoots()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var native = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var workspace = await native.Server.CreateApplicationWorkspaceAsync();
        var firstRoot = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "native-volume-tests", "shop");
        var otherRoot = Path.Combine(firstRoot, "..", "other", "shop");
        var expectedVolumes = new List<string>();
        ApplicationExecution? execution = null;
        foreach (var (root, prefix) in new (string, string?)[]
        {
            (firstRoot, null), (Path.Combine(firstRoot, "..", "shop"), null),
            (otherRoot, null), (otherRoot, "User.Exact_Prefix")
        })
        {
            var builder = DistributedApplication.CreateBuilder(prefix is null ? [] : [$"--native-volume-prefix={prefix}"],
                new() { ApplicationRoot = root });
            // Exercise the actual generic container + Redis data-volume authoring
            // operation; the executor records native plans, not client commands.
            var cache = builder.AddContainer(new RedisContainerResource("cache")).WithDataVolume();
            var volume = builder.GetDefaultDataVolumeName(cache.Resource);
            if (expectedVolumes.Count == 0 || expectedVolumes[^1] != volume)
            {
                expectedVolumes.Add(volume);
            }
            var revision = await workspace.BeginRevisionAsync();
            await builder.MaterializeAsync(revision, CancellationToken.None);
            await workspace.CommitRevisionAsync(revision);
            execution = execution is null ? await revision.StartApplicationAsync() : await workspace.GetApplicationExecutionAsync();
            await execution.WaitApplicationReadyAsync(10000);
            Assert.Equal(expectedVolumes.Count, executor.Plans.Count);
        }
        Assert.Equal(3, expectedVolumes.Count);
        Assert.Equal(expectedVolumes.Select(volume => new Aspire.Hosting.Native.Runtime.WorkloadMount(
            Aspire.Hosting.Native.Runtime.WorkloadMountKind.Volume, volume, "/data", false)),
            executor.Plans.Select(plan => Assert.Single(Assert.IsType<Aspire.Hosting.Native.Runtime.ContainerWorkload>(plan).Mounts)));
        await workspace.RetireApplicationWorkspaceAsync();
        Assert.Equal(3, executor.Removed.Count);
    }

    [Fact]
    public void OrdinaryProviderAuthoringCancellationIsInvocationContextNotAnExtraWireArgument()
    {
        var contract = RedisContracts::NativeIntegration.Generated.ProviderContract.Create();
        var capability = Assert.Single(contract.Metadata.GetProperty("capabilities").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "example.csharp.redis/addRedis");
        Assert.Equal(["composition", "name", "password"], capability.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()));
        var projected = typeof(RedisContracts::NativeIntegration.Generated.IntegrationExtensions)
            .GetMethod("AddRedisAsync")!;
        Assert.Equal([typeof(Composition), typeof(string), typeof(DeclaredValue), typeof(CancellationToken)],
            projected.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void FamiliarTypedCompositionAndBuildAreSynchronousAndPerformNoRpc()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var builder = DistributedApplication.CreateBuilder([$"--native-endpoint={listener.LocalEndpoint}"]);
        IResourceBuilder<RedisContainerResource> cache = builder.AddRedis("cache").WithDataVolume();
        IResourceBuilder<ExecutableResource> consumer = builder.AddExecutable("consumer", "unused-executable", ".")
            .WithReference(cache).WaitFor(cache);
        var application = builder.Build();
        Assert.IsType<RedisContainerResource>(cache.Resource);
        Assert.IsType<ExecutableResource>(consumer.Resource);
        Assert.Same(builder, cache.ApplicationBuilder);
        Assert.Same(builder, consumer.ApplicationBuilder);
        Assert.IsType<NativeApplication>(application);
        Assert.False(listener.Pending());
        Assert.False(typeof(NativeRemoteObject).IsAssignableFrom(cache.Resource.GetType()));
        Assert.Throws<InvalidOperationException>(() => cache.WithDataVolume("after-build"));
        Assert.Throws<InvalidOperationException>(() => builder.AddRedis("after-build"));
    }

    [Fact]
    public async Task CancelledRunDoesNotConnectAndDisposalCallersJoinExactlyOnce()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var builder = DistributedApplication.CreateBuilder([$"--native-endpoint={listener.LocalEndpoint}"]);
        var application = builder.Build();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await application.RunAsync(cancellation.Token);
        var first = application.DisposeAsync().AsTask();
        var second = application.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.False(listener.Pending());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => application.StartAsync());
    }

    [Fact]
    public void ReferencesRejectForeignTransportPlansWithoutConnecting()
    {
        var first = DistributedApplication.CreateBuilder([]);
        var second = DistributedApplication.CreateBuilder([]);
        var cache = first.AddRedis("cache");
        var consumer = second.AddExecutable("consumer", "unused-executable", ".");
        Assert.Throws<ArgumentException>(() => consumer.WithReference(cache));
        Assert.Throws<ArgumentException>(() => consumer.WaitFor(cache));
        Assert.Throws<NotSupportedException>(() => DistributedApplication.CreateBuilder(["--publish=true"]));
    }

    [Fact]
    public async Task IntegrationAuthorsComposeGenericContainersAndNativeValuesWithoutStartingWorkloads()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var native = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await native.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var builder = DistributedApplicationBuilder.ForIntegration();
        var cache = NativeIntegrations.Redis.Authoring.RedisBuilderExtensions.AddRedis(builder, "cache").WithDataVolume("owned-volume");
        var consumer = builder.AddExecutable("consumer", Environment.ProcessPath!, AppContext.BaseDirectory).WithReference(cache);
        var waiter = builder.AddExecutable("waiter", Environment.ProcessPath!, AppContext.BaseDirectory).WaitFor(cache);
        var bound = await builder.MaterializeAsync(composition, CancellationToken.None);
        var snapshot = await composition.InspectAsync();
        Assert.Equal(["cache", "consumer", "waiter"], snapshot.Resources.Select(resource => resource.Name));
        var producer = snapshot.Resources[0];
        Assert.Empty(snapshot.Resources[1].Dependencies);
        Assert.Equal([producer.ResourceId], snapshot.Resources[2].Dependencies);
        var consumerValues = (await bound.Declaration(consumer.Resource).InspectSymbolicConfigurationAsync()).Properties;
        Assert.Equal([producer.ResourceId], Assert.Single(consumerValues).References);
        Assert.True(Assert.Single(consumerValues, property => property.Name.Contains("ConnectionStrings__cache", StringComparison.Ordinal)).IsSecret);
        Assert.Empty((await bound.Declaration(waiter.Resource).InspectSymbolicConfigurationAsync()).Properties);
        Assert.True((await bound.Declaration(cache.Resource).InspectSymbolicConfigurationAsync()).Properties
            .Single(property => property.Name == "health.env.REDIS_PASSWORD").IsSecret);
        Assert.Equal("[native reference expression]", cache.Resource.ConnectionStringExpression.ToString());
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task GeneratedCredentialIsNativeOwnedAndRevokedWithItsGeneration()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var native = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await native.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var password = await composition.AddGeneratedSecretParameterAsync("generated");
        var resource = await composition.AddResourceAsync("target", "example/target");
        await resource.SetSymbolicConfigurationAsync("password", password);
        var property = Assert.Single((await resource.InspectSymbolicConfigurationAsync()).Properties);
        Assert.True(property.IsSecret);
        Assert.Empty((await resource.InspectResourceAsync()).Configuration);
        await Assert.ThrowsAsync<NativeCapabilityException>(() => composition.AddGeneratedSecretParameterAsync("generated"));
        await session.RetireGenerationAsync(composition);
        var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() => password.EncodeUriValueAsync());
        Assert.Equal("HANDLE_NOT_FOUND", failure.Code);
    }
}

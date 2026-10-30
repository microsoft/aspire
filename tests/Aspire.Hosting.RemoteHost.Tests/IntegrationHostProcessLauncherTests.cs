// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias RemoteHost;

using System.Diagnostics;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.Shared;
using Aspire.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using RemoteHostKnownConfigNames = RemoteHost::Aspire.Hosting.KnownConfigNames;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationHostProcessLauncherTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CancellationBeforeLaunchDoesNotCreateAnExecutionOrLogAnError()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new TestIntegrationHostProcessFactory();
        var logger = new RecordingLogger<IntegrationHostLauncher>();
        var launcher = new IntegrationHostProcessLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            IntegrationHostConfiguration.Default, RemoteHostProfilingTelemetry.Disabled, logger, factory, new FakeTimeProvider());
        var descriptor = new IntegrationHostDescriptor { Language = "test/launch", PackageName = "example", HostEntryPoint = "unused.mts" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync(descriptor, "attempt", cancellation.Token));

        Assert.Equal(0, factory.CreateCount);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Launch canceled for integration host 'example' [test/launch].", entry.Message);
    }

    [Fact]
    public async Task InvalidProcessIdentityDisposesTheStartedExecution()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new TestIntegrationHostProcessFactory { StartResult = true };
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["REMOTE_APP_HOST_SOCKET_PATH"] = "test-socket" }).Build());
        var launcher = new IntegrationHostProcessLauncher(
            new LanguageSupportResolver(services, () => [typeof(TestIntegrationLanguageSupport).Assembly], NullLogger<LanguageSupportResolver>.Instance),
            configuration, RemoteHostProfilingTelemetry.Disabled, NullLogger<IntegrationHostLauncher>.Instance, factory, new FakeTimeProvider());
        var descriptor = new IntegrationHostDescriptor { Language = "test/launch", PackageName = "example", HostEntryPoint = Path.Combine(AppContext.BaseDirectory, "host.mts") };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => launcher.LaunchAsync(descriptor, "attempt", TestContext.Current.CancellationToken));

        Assert.Equal(1, factory.StartCount);
        Assert.Equal(1, factory.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStart_DisposesTheExecutionAndPreservesConfiguration(bool throws)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var services = new ServiceCollection().BuildServiceProvider();
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<IntegrationHostLauncher>();
        var factory = new TestIntegrationHostProcessFactory
        {
            StartError = throws ? new InvalidOperationException("spawn failure") : null
        };
        var entryPoint = Path.Combine(workspace.Path, "entry point.mts");
        var descriptor = new IntegrationHostDescriptor { Language = "test/launch", PackageName = "example", HostEntryPoint = entryPoint };
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REMOTE_APP_HOST_SOCKET_PATH"] = "test-socket",
            [RemoteHostKnownConfigNames.RemoteAppHostToken] = "test-token",
            ["IntegrationHost:OutputDrainIdleTimeout"] = "00:00:10",
            ["IntegrationHost:ShutdownTimeout"] = "00:00:12"
        }).Build());
        var launcher = new IntegrationHostProcessLauncher(
            new LanguageSupportResolver(services, () => [typeof(TestIntegrationLanguageSupport).Assembly], NullLogger<LanguageSupportResolver>.Instance),
            configuration, RemoteHostProfilingTelemetry.Disabled, logger, factory, clock);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(descriptor, "attempt", TestContext.Current.CancellationToken));

        if (throws)
        {
            Assert.Same(factory.StartError, error);
        }
        else
        {
            Assert.Equal("Could not start integration host 'example'.", error.Message);
        }
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.StartCount);
        Assert.Equal(1, factory.DisposeCount);
        var startInfo = Assert.IsType<ProcessStartInfo>(factory.StartInfo);
        Assert.Equal(workspace.Path, startInfo.WorkingDirectory);
        Assert.Equal("test-socket", startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"]);
        Assert.Equal("test-token", startInfo.Environment[RemoteHostKnownConfigNames.RemoteAppHostToken]);
        Assert.Equal("attempt", startInfo.Environment[IntegrationHostLauncher.RegistrationIdVariable]);
        Assert.Equal(Environment.ProcessPath, startInfo.FileName);
        Assert.Equal([entryPoint, "space argument"], startInfo.ArgumentList);
        var options = Assert.IsType<ChildProcessOptions>(factory.Options);
        Assert.Equal(ChildProcessLifetime.OwnedTree, options.Lifetime);
        Assert.Same(clock, options.TimeProvider);
        Assert.Equal(TimeSpan.FromSeconds(10), options.OutputDrainIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), options.TerminationTimeout);
        Assert.Collection(logger.Entries,
            entry => Assert.Equal(LogLevel.Information, entry.Level),
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(error, entry.Exception);
                Assert.Equal("Failed to launch integration host 'example' [test/launch].", entry.Message);
            });
    }

    [Fact]
    public async Task LaunchAndCleanupFailure_PreservesBothCausesAndLogsTheFailure()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new TestIntegrationHostProcessFactory
        {
            StartError = new InvalidOperationException("spawn failure"),
            DisposeError = new IOException("cleanup failure")
        };
        var logger = new RecordingLogger<IntegrationHostLauncher>();
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["REMOTE_APP_HOST_SOCKET_PATH"] = "test-socket" }).Build());
        var launcher = new IntegrationHostProcessLauncher(
            new LanguageSupportResolver(services, () => [typeof(TestIntegrationLanguageSupport).Assembly], NullLogger<LanguageSupportResolver>.Instance),
            configuration, RemoteHostProfilingTelemetry.Disabled, logger, factory, new FakeTimeProvider());
        var descriptor = new IntegrationHostDescriptor { Language = "test/launch", PackageName = "example", HostEntryPoint = Path.Combine(AppContext.BaseDirectory, "host.mts") };

        var error = await Assert.ThrowsAsync<AggregateException>(() => launcher.LaunchAsync(descriptor, "attempt", TestContext.Current.CancellationToken));

        Assert.Collection(error.InnerExceptions,
            cause => Assert.Same(factory.StartError, cause),
            cause => Assert.Same(factory.DisposeError, cause));
        Assert.Equal(1, factory.DisposeCount);
        Assert.Same(error, logger.Entries.Last().Exception);
        Assert.Equal(LogLevel.Error, logger.Entries.Last().Level);
    }
}

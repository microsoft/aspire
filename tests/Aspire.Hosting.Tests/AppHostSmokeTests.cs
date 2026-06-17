// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Dcp;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class AppHostSmokeTests
{
    [Theory]
    [InlineData("invalid-locale", "Invalid locale", null)]
    [InlineData("", null, null)]
    [InlineData("en-US", null, "en-US")]
    [InlineData("fr", null, "fr")]
    [InlineData("fr", null, "fr", "DOTNET_CLI_UI_LANGUAGE")]
    [InlineData("el", "Unsupported locale", null)]
    public void LocaleOverrideReturnsExitCode(string locale, string? expectedLocaleError, string? expectedCulture, string environmentVariableName = "ASPIRE_LOCALE_OVERRIDE")
    {
        var dcpOptions = GetDcpOptions();

        // Need to configure DCP details because they're not present in the remote executor assembly.
        var remoteInvokeOptions = new RemoteInvokeOptions();
        remoteInvokeOptions.StartInfo.Environment[$"DcpPublisher__CliPath"] = dcpOptions.CliPath;
        remoteInvokeOptions.StartInfo.Environment[$"DcpPublisher__DashboardPath"] = dcpOptions.DashboardPath;

        RemoteExecutor.Invoke(RunTest, locale, expectedLocaleError ?? string.Empty, expectedCulture ?? string.Empty, environmentVariableName, remoteInvokeOptions).Dispose();

        static async Task RunTest(string loc, string expectedLocaleError, string expectedCulture, string envVar)
        {
            // Arrange
            var originalCulture = CultureInfo.CurrentCulture;

            var testSink = new TestSink();

            Environment.SetEnvironmentVariable(envVar, loc);

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                DisableDashboard = true
            });

            var testService = new TestHostedService();
            builder.Services.AddHostedService(sp => testService);
            builder.Services.AddSingleton<ILoggerProvider>(new TestLoggerProvider(testSink));

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = builder.Build();

            // Act
            await host.StartAsync().DefaultTimeout();
            await host.StopAsync().DefaultTimeout();

            // Assert
            if (!string.IsNullOrEmpty(expectedCulture))
            {
                Assert.NotNull(testService.AppCultureInfo);
                Assert.Equal(expectedCulture, testService.AppCultureInfo.Name);
            }

            if (!string.IsNullOrEmpty(expectedLocaleError))
            {
                var writes = testSink.Writes.ToArray();
                var errorLog = testSink.Writes.SingleOrDefault(w => w.Message?.Contains(expectedLocaleError) ?? false);
                Assert.True(
                    errorLog != null,
                    $"""
                    Expected error log not found: {expectedLocaleError}
                    Actual logs: {string.Join(", ", writes.Select(w => w.Message))}
                    """);
            }

            // Test culture is reset
            Assert.Equal(originalCulture, CultureInfo.CurrentCulture);
        }

        static DcpOptions GetDcpOptions()
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                DisableDashboard = true
            });
            var host = builder.Build();
            var dcpOptions = host.Services.GetRequiredService<IOptions<DcpOptions>>();
            return dcpOptions.Value;
        }
    }

    private sealed class TestHostedService : IHostedLifecycleService
    {
        public CultureInfo? AppCultureInfo { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StartingAsync(CancellationToken cancellationToken)
        {
            AppCultureInfo = CultureInfo.CurrentCulture;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

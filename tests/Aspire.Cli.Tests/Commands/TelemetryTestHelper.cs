// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

internal static class TelemetryTestHelper
{
    /// <summary>
    /// A fixed base time used by telemetry tests. All test timestamps should be expressed
    /// as offsets from this value (e.g. <c>s_testTime.AddMilliseconds(50)</c>).
    /// </summary>
    internal static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Converts a <see cref="DateTime"/> to Unix nanoseconds (nanoseconds since the Unix epoch).
    /// </summary>
    internal static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeSinceEpoch = dateTime.ToUniversalTime() - unixEpoch;

        return (ulong)timeSinceEpoch.Ticks * 100;
    }

    /// <summary>
    /// Creates an <see cref="OtlpResourceJson"/> with the specified service name and optional instance ID.
    /// </summary>
    internal static OtlpResourceJson CreateOtlpResource(string serviceName, string? instanceId)
    {
        var attrs = new List<OtlpKeyValueJson>
        {
            new() { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } },
        };
        if (instanceId is not null)
        {
            attrs.Add(new() { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = instanceId } });
        }
        return new OtlpResourceJson { Attributes = [.. attrs] };
    }

    /// <summary>
    /// Creates a fully configured <see cref="ServiceProvider"/> for telemetry command tests,
    /// with a mock backchannel and HTTP handler that serves resource and telemetry data.
    /// </summary>
    /// <param name="workspace">The temporary workspace for the test.</param>
    /// <param name="outputHelper">The xUnit test output helper.</param>
    /// <param name="outputWriter">The test output writer to capture console output.</param>
    /// <param name="resources">The resource list returned by the /api/telemetry/resources endpoint.</param>
    /// <param name="telemetryEndpoints">
    /// A dictionary mapping URL substrings (e.g. "/api/telemetry/logs") to their JSON response content.
    /// </param>
    internal static ServiceProvider CreateTelemetryTestServices(
        TemporaryWorkspace workspace,
        ITestOutputHelper outputHelper,
        TestOutputTextWriter outputWriter,
        ResourceInfoJson[] resources,
        Dictionary<string, string> telemetryEndpoints)
    {
        var resourcesJson = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:18888",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888/login?t=test"],
                IsHealthy = true
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesJson, System.Text.Encoding.UTF8, "application/json")
                };
            }

            foreach (var (urlPattern, json) in telemetryEndpoints)
            {
                if (url.Contains(urlPattern))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });

        // Register the handler as a singleton so it is disposed with the ServiceProvider,
        // rather than at the end of this method (which would cause ObjectDisposedException).
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        return services.BuildServiceProvider();
    }
}

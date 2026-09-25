// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Hosting;

namespace Aspire.Cli.Profiling;

internal sealed class ProfileCaptureEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues;

    private ProfileCaptureEnvironment(Dictionary<string, string?> previousValues)
    {
        _previousValues = previousValues;
    }

    public static ProfileCaptureEnvironment Apply(ProfileCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var values = CreateValues(options);

        // The CLI telemetry pipeline is configured from ambient process environment at startup.
        // Mutating process state here is intentional because this runs before DI creates
        // TelemetryManager; child AppHost processes get an explicit copy via AddCurrentToEnvironment.
        // Environment variable names are case-insensitive on Windows, so use the matching comparer
        // to avoid storing two restore entries for what the OS treats as the same variable.
        var previousValues = new Dictionary<string, string?>(StringComparers.EnvironmentVariableName);
        foreach (var (name, value) in values)
        {
            previousValues[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        return new ProfileCaptureEnvironment(previousValues);
    }

    public static void AddCurrentToEnvironment(IDictionary<string, string> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        foreach (var name in CreateValues().Keys)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environmentVariables[name] = value;
            }
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Dictionary<string, string?> CreateValues(ProfileCaptureOptions? options = null)
    {
        var otlpGrpcUrl = options?.OtlpGrpcUrl;
        var sessionId = options?.SessionId;

        // Set standard OTEL variables for both this CLI process and the AppHost process it launches.
        // AppHosts support these non-prefixed variables directly, so we do not need separate
        // ASPIRE_OTEL_* copies for the profiling path.
        // OTEL env var conventions: https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/
        return new Dictionary<string, string?>(StringComparers.EnvironmentVariableName)
        {
            [AspireCliTelemetry.TelemetryOptOutConfigKey] = "true",
            [KnownConfigNames.ProfilingEnabled] = "true",
            [KnownConfigNames.Legacy.StartupProfilingEnabled] = "true",
            [KnownConfigNames.ProfilingSessionId] = sessionId,
            [KnownConfigNames.Legacy.StartupOperationId] = sessionId,
            [KnownOtelConfigNames.ExporterOtlpEndpoint] = otlpGrpcUrl,
            [KnownOtelConfigNames.ExporterOtlpProtocol] = "grpc",
            [KnownOtelConfigNames.BspScheduleDelay] = "1000"
        };
    }
}

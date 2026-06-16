// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

/// <summary>
/// A test fixture that sets up an <see cref="ActivityListener"/> and <see cref="AspireCliTelemetry"/>
/// for testing telemetry-related functionality.
/// </summary>
internal sealed class TelemetryFixture : IDisposable
{
    private readonly ActivityListener _listener;

    /// <summary>
    /// Creates a new telemetry fixture with unique activity source names.
    /// </summary>
    /// <param name="machineInfoProvider">Optional machine information provider. Uses a default test provider if not specified.</param>
    /// <param name="ciEnvironmentDetector">Optional CI environment detector. Uses a default test detector if not specified.</param>
    /// <param name="codingAgentDetector">Optional coding agent detector. Uses a default test detector if not specified.</param>
    /// <param name="logger">Optional logger. Uses <see cref="NullLogger"/> if not specified.</param>
    /// <param name="sampleResult">The sampling result for the activity listener. Defaults to <see cref="ActivitySamplingResult.AllDataAndRecorded"/>.</param>
    /// <param name="executionContext">Optional CLI execution context. Defaults to a local-identity context so the telemetry's required context is always satisfied.</param>
    public TelemetryFixture(
        IMachineInformationProvider? machineInfoProvider = null,
        ICIEnvironmentDetector? ciEnvironmentDetector = null,
        ICodingAgentDetector? codingAgentDetector = null,
        ILogger<AspireCliTelemetry>? logger = null,
        ActivitySamplingResult sampleResult = ActivitySamplingResult.AllDataAndRecorded,
        CliExecutionContext? executionContext = null)
    {
        ReportedSourceName = $"Test.{Path.GetRandomFileName()}";
        DiagnosticsSourceName = $"Test.{Path.GetRandomFileName()}";

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReportedSourceName || source.Name == DiagnosticsSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampleResult,
            ActivityStopped = activity => CapturedActivity = activity
        };
        ActivitySource.AddActivityListener(_listener);

        machineInfoProvider ??= new TestMachineInformationProvider();
        ciEnvironmentDetector ??= new TestCIEnvironmentDetector();
        codingAgentDetector ??= new TestCodingAgentDetector();
        logger ??= NullLogger<AspireCliTelemetry>.Instance;
        executionContext ??= Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory));

        Telemetry = new AspireCliTelemetry(logger, machineInfoProvider, ciEnvironmentDetector, codingAgentDetector, ReportedSourceName, DiagnosticsSourceName, executionContext);
        Telemetry.InitializeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the name of the reported activity source.
    /// </summary>
    public string ReportedSourceName { get; }

    /// <summary>
    /// Gets the name of the diagnostics activity source.
    /// </summary>
    public string DiagnosticsSourceName { get; }

    /// <summary>
    /// Gets the initialized telemetry instance.
    /// </summary>
    public AspireCliTelemetry Telemetry { get; }

    /// <summary>
    /// Gets the last activity that was stopped by the listener.
    /// </summary>
    public Activity? CapturedActivity { get; private set; }

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// A test implementation of <see cref="IMachineInformationProvider"/> with configurable values.
    /// </summary>
    internal sealed class TestMachineInformationProvider : IMachineInformationProvider
    {
        public string? DeviceId { get; set; } = "test-device-id";
        public string MacAddressHash { get; set; } = "test-mac-hash";
        public string UserName { get; set; } = string.Empty;
        public string UserDomainName { get; set; } = string.Empty;

        public Task<string?> GetOrCreateDeviceId() => Task.FromResult(DeviceId);
        public Task<string> GetMacAddressHash() => Task.FromResult(MacAddressHash);
    }

    /// <summary>
    /// A test implementation of <see cref="ICIEnvironmentDetector"/> with configurable result.
    /// </summary>
    internal sealed class TestCIEnvironmentDetector : ICIEnvironmentDetector
    {
        public bool IsCIEnvironmentResult { get; set; }

        public bool IsCIEnvironment() => IsCIEnvironmentResult;
    }

    /// <summary>
    /// A test implementation of <see cref="ICodingAgentDetector"/> with configurable result.
    /// </summary>
    internal sealed class TestCodingAgentDetector : ICodingAgentDetector
    {
        public string? CodingAgent { get; set; }

        public string? GetCodingAgent() => CodingAgent;
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Aspire.Shared.Telemetry;
using Microsoft.AspNetCore.InternalTesting;
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
    /// <param name="internalMicrosoftDetector">Optional internal Microsoft detector. Uses a default test detector if not specified.</param>
    /// <param name="logger">Optional logger. Uses <see cref="NullLogger"/> if not specified.</param>
    /// <param name="sampleResult">The sampling result for the activity listener. Defaults to <see cref="ActivitySamplingResult.AllDataAndRecorded"/>.</param>
    /// <param name="executionContext">Optional CLI execution context. Defaults to a local-identity context so the telemetry's required context is always satisfied.</param>
    /// <param name="telemetryConfiguration">Optional telemetry configuration. Uses reported telemetry defaults if not specified.</param>
    /// <param name="initialize">Whether to initialize telemetry and wait for completion before returning.</param>
    public TelemetryFixture(
        IMachineInformationProvider? machineInfoProvider = null,
        ICIEnvironmentDetector? ciEnvironmentDetector = null,
        ICodingAgentDetector? codingAgentDetector = null,
        IInternalMicrosoftDetector? internalMicrosoftDetector = null,
        ILogger<AspireCliTelemetry>? logger = null,
        ActivitySamplingResult sampleResult = ActivitySamplingResult.AllDataAndRecorded,
        CliExecutionContext? executionContext = null,
        TelemetryConfiguration? telemetryConfiguration = null,
        bool initialize = true)
    {
        ReportedSourceName = $"Test.{Path.GetRandomFileName()}";
        DiagnosticsSourceName = $"Test.{Path.GetRandomFileName()}";

        machineInfoProvider ??= new TestMachineInformationProvider();
        ciEnvironmentDetector ??= new TestCIEnvironmentDetector();
        codingAgentDetector ??= new TestCodingAgentDetector();
        internalMicrosoftDetector ??= new TestInternalMicrosoftDetector();
        logger ??= NullLogger<AspireCliTelemetry>.Instance;
        executionContext ??= Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory));
        TagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        Telemetry = new AspireCliTelemetry(logger,
            machineInfoProvider, ciEnvironmentDetector, codingAgentDetector, internalMicrosoftDetector,
            telemetryConfiguration ?? new TelemetryConfiguration { ReportedTelemetryEnabled = true },
            ReportedSourceName, DiagnosticsSourceName, executionContext, TagsSource);
        Telemetry.SetEventLogger(logger);

        // Simulate ActivityTagEnrichmentProcessor behavior: in production, tags are added
        // in OnEnd before export. Tests assert on live activities before they
        // stop, so we add tags in ActivityStarted instead to make them visible immediately.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReportedSourceName || source.Name == DiagnosticsSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampleResult,
            ActivityStarted = activity =>
            {
                if (TagsSource.TagsTask is { IsCompletedSuccessfully: true } tagsTask)
                {
                    foreach (var tag in tagsTask.Result)
                    {
                        if (activity.OperationName == TelemetryConstants.Activities.InternalMicrosoftDetector &&
                            tag.Key is TelemetryConstants.Tags.InternalMicrosoftAlias or TelemetryConstants.Tags.InternalMicrosoftDomain)
                        {
                            continue;
                        }

                        Telemetry.SetActivityProperty(activity, tag.Key, tag.Value);
                    }
                }
            },
            ActivityStopped = activity => CapturedActivity = activity
        };
        ActivitySource.AddActivityListener(_listener);

        if (initialize)
        {
            Telemetry.Initialize();
            // Wait for background tag calculation to complete so tests can assert on tags.
            Telemetry.GetDefaultTagsAsync().DefaultTimeout().GetAwaiter().GetResult();
        }
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
    /// Gets the tags source used by this fixture.
    /// </summary>
    public TelemetryTagsSource TagsSource { get; }

    /// <summary>
    /// Gets the initialized telemetry instance.
    /// </summary>
    public AspireCliTelemetry Telemetry { get; }

    /// <summary>
    /// Gets the last activity that was stopped by the listener.
    /// </summary>
    public Activity? CapturedActivity { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Telemetry.Dispose();
        _listener.Dispose();
    }

    /// <summary>
    /// A test implementation of <see cref="IMachineInformationProvider"/> with configurable values.
    /// </summary>
    internal sealed class TestMachineInformationProvider : Aspire.Tests.Shared.Telemetry.TestMachineInformationProvider;

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

    /// <summary>
    /// A test implementation of <see cref="IInternalMicrosoftDetector"/> with configurable result.
    /// </summary>
    internal sealed class TestInternalMicrosoftDetector : Aspire.Tests.Shared.Telemetry.TestInternalMicrosoftDetector;
}

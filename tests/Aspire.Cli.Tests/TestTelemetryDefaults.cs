// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests;

/// <summary>
/// Opts the CLI out of Azure Monitor telemetry for the entire <c>Aspire.Cli.Tests</c>
/// process. Tests that need to exercise the Azure Monitor branch override this via the
/// in-memory configuration passed to <see cref="Aspire.Cli.Program.BuildApplicationAsync"/>,
/// which is layered on top of <c>AddEnvironmentVariables()</c> and therefore wins.
///
/// Why opt out by default in tests: see https://github.com/microsoft/aspire/issues/17450.
/// Azure Monitor's default <c>RateLimitedSampler</c> emits a <c>microsoft.sample_rate</c>
/// attribute via <c>ActivityCreationOptions.SamplingTags.Add</c> (no <c>TryAdd</c>). When
/// xUnit v3 runs test classes in parallel and more than one <c>TelemetryManager</c>
/// builds a TracerProvider in the same process, two listeners are registered on the
/// <c>Aspire.Cli.Reported</c> ActivitySource and both samplers fire on the same shared
/// <c>ActivityCreationOptions</c>, so the second <c>Add</c> throws
/// <c>InvalidOperationException("The collection already contains item with same key 'microsoft.sample_rate'")</c>.
/// Defaulting the test process to opted-out keeps Azure Monitor out of the pipeline
/// except in the focused tests that explicitly need it.
/// </summary>
internal static class TestTelemetryDefaults
{
    [ModuleInitializer]
    internal static void OptOutByDefault()
    {
        Environment.SetEnvironmentVariable(AspireCliTelemetry.TelemetryOptOutConfigKey, "true");
    }
}

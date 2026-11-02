// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource whose completion contributes to the lifetime and outcome of an <c>aspire test</c> run.
/// </summary>
/// <remarks>
/// The annotation does not change normal application lifetimes, but marks dashboard resources as testing.
/// Test resources must publish a terminal
/// resource state and an exit code; zero indicates success. Failure to start also indicates failure.
/// A collector configured through <c>WithTesting</c> publishes individual results alongside
/// resource execution. Both execution and collection contribute to the test run's completion.
/// </remarks>
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestingAnnotation : IResourceAnnotation
{
    private Value? _dashboardResults;

    internal Value? DashboardResults
    {
        get => Volatile.Read(ref _dashboardResults);
        set => Volatile.Write(ref _dashboardResults, value);
    }

    /// <summary>
    /// Creates an annotation marking a test resource for resource-based completion.
    /// </summary>
    public TestingAnnotation()
    {
    }

    internal Func<ResourceTestingContext, Task>? Collector { get; init; }
    internal TestingContext? TestingContext { get; init; }
    internal string? OutputPath { get; init; }
}

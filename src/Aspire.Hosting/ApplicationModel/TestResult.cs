// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes an individual result published by a framework-neutral resource collector.
/// </summary>
/// <remarks>
/// Collectors translate their framework's results into this shape. Resource identity is supplied
/// by the collection context, not by the result. A result does not replace the resource's process
/// completion outcome, and no individual results are inferred from a process exit code.
/// </remarks>
/// <example>
/// <code lang="C#">
/// context.PublishResult(new TestResult
/// {
///     Name = "checkout accepts an order",
///     Outcome = TestOutcome.Passed,
///     Duration = TimeSpan.FromMilliseconds(25)
/// });
/// </code>
/// </example>
[AspireDto]
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestResult
{
    /// <summary>
    /// Gets the framework-provided name of the result.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the observed outcome of the test.
    /// </summary>
    public required TestOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the observed duration, or null when the framework does not provide one.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Gets an optional framework diagnostic, such as a failure or skip reason.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Defines the outcomes that a resource test collector can publish.
/// </summary>
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public enum TestOutcome
{
    /// <summary>
    /// The test passed.
    /// </summary>
    Passed = 1,

    /// <summary>
    /// The test failed.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The test was skipped.
    /// </summary>
    Skipped = 3
}

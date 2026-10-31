// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource whose completion contributes to the lifetime and outcome of an <c>aspire test</c> run.
/// </summary>
/// <remarks>
/// The annotation has no effect on normal application runs. Test resources must publish a terminal
/// resource state and an exit code; zero indicates success. Failure to start also indicates failure.
/// Collectors configured through <see cref="TestingOptions"/> publish individual results alongside
/// resource execution. Both execution and collection contribute to the test run's completion.
/// </remarks>
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestingAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Creates an annotation marking a test resource with optional collector configuration.
    /// </summary>
    /// <param name="options">Configured testing options, or null for resource-based completion without collectors.</param>
    public TestingAnnotation(TestingOptions? options = null)
    {
        Options = options;
    }

    /// <summary>
    /// Gets the resource's configured testing options.
    /// </summary>
    public TestingOptions? Options { get; }
}

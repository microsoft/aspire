// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides the output destination for the current <c>aspire test</c> invocation.
/// </summary>
/// <remarks>
/// The CLI creates the directory before launching the AppHost. Test setup callbacks run before
/// resources start and can configure arguments, environment variables, or container bind mounts.
/// This is a host filesystem path; containers must explicitly mount it at their chosen destination.
/// </remarks>
/// <example>
/// <code lang="C#">
/// var tests = builder.AddExecutable("tests", "test-runner", ".");
/// tests.WithTestRun(context =>
/// {
///     tests.WithEnvironment("TEST_OUTPUT_DIRECTORY", context.OutputPath);
///     return Task.CompletedTask;
/// });
/// </code>
/// </example>
[AspireExport]
public sealed class TestRunContext
{
    internal TestRunContext(string outputPath, CancellationToken cancellationToken)
    {
        OutputPath = outputPath;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the absolute host path to the unique output directory for this invocation.
    /// </summary>
    [AspireExport]
    public string OutputPath { get; }

    /// <summary>
    /// Gets the token used to cancel test resource setup.
    /// </summary>
    [AspireExport]
    public CancellationToken CancellationToken { get; }
}

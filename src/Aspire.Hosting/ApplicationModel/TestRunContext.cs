// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides the output destination for the current <c>aspire test</c> invocation.
/// </summary>
/// <remarks>
/// The CLI creates the directory before launching the AppHost, so testing configuration can run
/// immediately during model definition. Resource collectors use their own isolated output paths
/// beneath this run directory. This is a host filesystem path, not an in-container path.
/// </remarks>
/// <example>
/// <code lang="C#">
/// var tests = builder.AddExecutable("tests", "test-runner", ".");
/// tests.WithTesting(options =>
/// {
///     if (options.OutputPath is { } path)
///     {
///         tests.WithEnvironment("TEST_OUTPUT_DIRECTORY", path);
///     }
/// });
/// </code>
/// </example>
[AspireExport]
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestRunContext
{
    internal TestRunContext(string outputPath)
    {
        OutputPath = outputPath;
    }

    /// <summary>
    /// Gets the absolute host path to the unique output directory for this invocation.
    /// </summary>
    [AspireExport]
    public string OutputPath { get; }

}

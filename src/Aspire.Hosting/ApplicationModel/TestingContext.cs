// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides the output destination for the current <c>aspire test</c> invocation.
/// </summary>
/// <remarks>
/// The CLI creates the directory before launching the AppHost. Runtime collectors use their own isolated output paths
/// beneath this run directory. This is a host filesystem path, not an in-container path.
/// </remarks>
/// <example>
/// <code lang="C#">
/// var tests = builder.AddExecutable("tests", "test-runner", ".");
/// tests.WithTesting(async context =>
/// {
///     await context.WaitForCompletionAsync();
///     await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "collected.txt"),
///         "Collected test artifacts", context.CancellationToken);
/// });
/// </code>
/// </example>
[AspireExport]
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestingContext
{
    internal TestingContext(string outputPath)
    {
        OutputPath = outputPath;
    }

    /// <summary>
    /// Gets the absolute host path to the unique output directory for this invocation.
    /// </summary>
    [AspireExport]
    public string OutputPath { get; }

}

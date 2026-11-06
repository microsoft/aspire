// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;

namespace AnalyzeCiFailure;

/// <summary>Binds commands to invocation context and cancellation, with checked execution for fatal failures.</summary>
internal sealed class AnalyzerCommandExecutor(
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null,
    CancellationToken cancellationToken = default)
{
    internal async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? outputFile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The shared runner drains both streams. Observe orderly cancellation at command
        // boundaries so the collector can finalize progress without changing shared tooling.
        var result = await ProcessRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            environment,
            outputFile);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal async Task<string> ExecuteAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(executable, arguments);
        if (!(result.ExitCode == 0))
        {
            throw new ValidationException($"{executable} failed ({result.ExitCode}): {CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(result.Error, limit: 4096))}");
        }

        return result.Output;
    }
}

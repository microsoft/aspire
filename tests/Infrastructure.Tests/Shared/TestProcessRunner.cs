// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;
using Xunit;

namespace Infrastructure.Tests;

internal static class TestProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        ITestOutputHelper output,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        output.WriteLine($"Executing: {fileName} {string.Join(' ', arguments)} in {workingDirectory}");
        var execution = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            environment);
        var result = new ProcessResult(execution.ExitCode, execution.Output, execution.Error);
        if (result.Output.Length > 0)
        {
            output.WriteLine(result.Output);
        }

        return result;
    }
}

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}

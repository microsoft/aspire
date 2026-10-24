// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace AnalyzeCiFailure;

// Sanitization and comment rendering remain single-sourced with publication.
internal sealed class PublicationScripts(string workingDirectory, string scriptsDirectory)
{
    public async Task<bool> SanitizeAsync(string command, string input, string output)
        => (await RunAsync("analyze-ci-failure-persistence.sh", [command, input, output])).ExitCode == 0;

    public async Task SanitizeInPlaceAsync(string command, string file)
    {
        var output = file + ".tmp";
        try
        {
            ValidationRules.Require(await SanitizeAsync(command, file, output), $"Unable to sanitize {Json.Display(Path.GetFileName(file))}");
            File.Move(output, file, overwrite: true);
        }
        finally
        {
            File.Delete(output);
        }
    }

    public async Task<int?> CommentBytesAsync(string analysis, string jobs, string url)
    {
        var result = await RunAsync("analyze-ci-failure-comment.sh", [analysis, jobs, url]);
        return result.ExitCode == 0 ? result.Bytes : null;
    }

    private async Task<(int ExitCode, int Bytes)> RunAsync(string script, string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(scriptsDirectory, script));
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        // Drain both pipes concurrently; rendered comments can exceed pipe capacity.
        using var stdout = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var readError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await readOutput;
        await Console.Error.WriteAsync(await readError);

        return (process.ExitCode, checked((int)stdout.Length));
    }
}

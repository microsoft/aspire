// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.TestTools;

public static class GitHubCli
{
    private const string FixtureDirectoryEnvironmentVariable = "ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR";

    public static async Task<JsonDocument> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        var stdout = await GetStringAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(stdout);
    }

    public static Task<string> GetStringAsync(string endpoint, CancellationToken cancellationToken)
        => GetStringAsync(endpoint, allowEscapeSequences: false, cancellationToken);

    // allowEscapeSequences passes `--allow-escape-sequences` to `gh api`. Required for endpoints whose
    // payload can contain terminal control characters; `gh` refuses to write those to a non-TTY stdout
    // and fails the whole call otherwise.
    public static Task<string> GetStringAsync(string endpoint, bool allowEscapeSequences, CancellationToken cancellationToken)
    {
        if (TryGetFixturePath(endpoint, ".json", out var fixturePath))
        {
            return File.ReadAllTextAsync(fixturePath, cancellationToken);
        }

        if (TryGetFixturePath(endpoint, ".txt", out fixturePath))
        {
            return File.ReadAllTextAsync(fixturePath, cancellationToken);
        }

        if (TryGetFixturePath(endpoint, ".err", out fixturePath))
        {
            return Task.FromException<string>(new InvalidOperationException(File.ReadAllText(fixturePath)));
        }

        return RunGhAsync(BuildApiArguments(endpoint, allowEscapeSequences), cancellationToken);
    }

    private static IReadOnlyList<string> BuildApiArguments(string endpoint, bool allowEscapeSequences)
    {
        List<string> arguments = ["api", "-H", "Accept: application/vnd.github+json"];
        if (allowEscapeSequences)
        {
            arguments.Add("--allow-escape-sequences");
        }

        arguments.Add(endpoint);

        return arguments;
    }

    public static async Task DownloadFileAsync(string endpoint, string outputPath, CancellationToken cancellationToken)
    {
        var outputExtension = Path.GetExtension(outputPath);
        if (TryGetFixturePath(endpoint, outputExtension, out var fixturePath) || TryGetFixturePath(endpoint, ".bin", out fixturePath))
        {
            File.Copy(fixturePath, outputPath, overwrite: true);
            return;
        }

        await RunGhToFileAsync(
            ["api", "-H", "Accept: application/vnd.github+json", endpoint],
            outputPath,
            cancellationToken).ConfigureAwait(false);
    }

    private static readonly TimeSpan s_defaultProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Test seam that replaces the string-returning <c>gh</c> invocation so tests can observe the argument
    /// list that would be passed to the process. Production code never sets this.
    /// </summary>
    internal static Func<IReadOnlyList<string>, CancellationToken, Task<string>>? GhInvokerOverride { get; set; }

    private static async Task<string> RunGhAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (GhInvokerOverride is { } invoker)
        {
            return await invoker(arguments, cancellationToken).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(s_defaultProcessTimeout);

        ProcessStartInfo processStartInfo = new()
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = processStartInfo
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"gh {BuildDisplayArguments(arguments)} failed: {message.Trim()}");
        }

        return stdout;
    }

    private static async Task RunGhToFileAsync(IReadOnlyList<string> arguments, string outputPath, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(s_defaultProcessTimeout);

        ProcessStartInfo processStartInfo = new()
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = processStartInfo
        };

        process.Start();

        string stderr;
        {
            using var outputStream = File.Create(outputPath);
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream, cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await Task.WhenAll(process.WaitForExitAsync(cts.Token), stdoutTask, stderrTask).ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            catch
            {
                try { stderr = await stderrTask.ConfigureAwait(false); } catch { stderr = string.Empty; }
                throw;
            }
        }

        // Stream is disposed above so file handle is released before delete (required on Windows).
        if (process.ExitCode != 0)
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            throw new InvalidOperationException($"gh {BuildDisplayArguments(arguments)} failed: {stderr.Trim()}");
        }
    }

    private static string BuildDisplayArguments(IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();

        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            var argument = arguments[i];
            builder.Append(argument.Contains(' ') ? $"\"{argument}\"" : argument);
        }

        return builder.ToString();
    }

    private static bool TryGetFixturePath(string endpoint, string extension, out string fixturePath)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable(FixtureDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            fixturePath = string.Empty;
            return false;
        }

        var fileName = Regex.Replace(endpoint, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        fixturePath = Path.Combine(fixtureDirectory, $"{fileName}{extension}");
        return File.Exists(fixturePath);
    }
}

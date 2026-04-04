// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
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

        return RunGhAsync(["api", "-H", "Accept: application/vnd.github+json", endpoint], cancellationToken);
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

    public static async Task<(int Number, string Url)> CreateIssueAsync(
        string repository,
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        if (TryGetFixtureContent("create-issue", ".json", out var fixtureContent))
        {
            using var document = JsonDocument.Parse(fixtureContent);
            var root = document.RootElement;
            return (root.GetProperty("number").GetInt32(), root.GetProperty("url").GetString()!);
        }

        List<string> arguments = ["issue", "create", "--repo", repository, "--title", title, "--body", body];

        foreach (var label in labels)
        {
            arguments.Add("--label");
            arguments.Add(label);
        }

        var stdout = await RunGhAsync(arguments, cancellationToken).ConfigureAwait(false);

        // gh issue create outputs the issue URL on stdout
        var issueUrl = stdout.Trim();
        var issueNumber = 0;

        // Extract issue number from URL: https://github.com/owner/repo/issues/123
        var lastSlash = issueUrl.LastIndexOf('/');
        if (lastSlash >= 0 && int.TryParse(issueUrl[(lastSlash + 1)..], out var parsed))
        {
            issueNumber = parsed;
        }

        return (issueNumber, issueUrl);
    }

    /// <summary>
    /// Searches for an existing failing-test issue by metadata marker.
    /// Returns the first matching issue (prefers open, then closed), or null if none found.
    /// </summary>
    public static async Task<(int Number, string Url, string State)?> SearchExistingIssueAsync(
        string repository,
        string metadataMarker,
        CancellationToken cancellationToken)
    {
        if (TryGetFixtureContent("search-issue", ".json", out var fixtureContent))
        {
            using var document = JsonDocument.Parse(fixtureContent);
            var root = document.RootElement;
            if (root.TryGetProperty("number", out var numberElement))
            {
                return (numberElement.GetInt32(), root.GetProperty("url").GetString()!, root.GetProperty("state").GetString()!);
            }

            return null;
        }

        var escapedMarker = metadataMarker.Replace("\"", "\\\"");
        var query = $"repo:{repository} is:issue label:failing-test in:body \"{escapedMarker}\"";

        using var searchDocument = await GetJsonAsync(
            $"search/issues?q={Uri.EscapeDataString(query)}&per_page=20",
            cancellationToken).ConfigureAwait(false);

        var items = searchDocument.RootElement.GetProperty("items");

        // Prefer open issues, then closed issues (matching workflow JS logic).
        JsonElement? openMatch = null;
        JsonElement? closedMatch = null;

        foreach (var item in items.EnumerateArray())
        {
            // Skip pull requests
            if (item.TryGetProperty("pull_request", out _))
            {
                continue;
            }

            var state = item.GetProperty("state").GetString();
            if (state is "open" && openMatch is null)
            {
                openMatch = item;
                break; // Open match is highest priority
            }
            else if (state is "closed" && closedMatch is null)
            {
                closedMatch = item;
            }
        }

        var match = openMatch ?? closedMatch;
        if (match is null)
        {
            return null;
        }

        return (
            match.Value.GetProperty("number").GetInt32(),
            match.Value.GetProperty("html_url").GetString()!,
            match.Value.GetProperty("state").GetString()!);
    }

    /// <summary>
    /// Reopens a closed issue.
    /// </summary>
    public static async Task ReopenIssueAsync(string repository, int issueNumber, CancellationToken cancellationToken)
    {
        if (TryGetFixtureContent("reopen-issue", ".json", out _))
        {
            return;
        }

        var issueNumberString = issueNumber.ToString(CultureInfo.InvariantCulture);

        // Use the API to set the state to open
        await RunGhAsync(
            ["api", "-X", "PATCH", $"repos/{repository}/issues/{issueNumberString}", "-f", "state=open"],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a comment to an issue.
    /// </summary>
    public static async Task AddIssueCommentAsync(string repository, int issueNumber, string body, CancellationToken cancellationToken)
    {
        if (TryGetFixtureContent("add-issue-comment", ".json", out _))
        {
            return;
        }

        await RunGhAsync(
            ["issue", "comment", issueNumber.ToString(CultureInfo.InvariantCulture), "--repo", repository, "--body", body],
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetFixtureContent(string name, string extension, out string content)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable(FixtureDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            var fixturePath = Path.Combine(fixtureDirectory, $"{name}{extension}");
            if (File.Exists(fixturePath))
            {
                content = File.ReadAllText(fixturePath);
                return true;
            }
        }

        content = string.Empty;
        return false;
    }

    private static readonly TimeSpan s_defaultProcessTimeout = TimeSpan.FromMinutes(5);

    private static async Task<string> RunGhAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.TestUtilities;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for MCP docs-based tooling.
/// These tests exercise the full MCP protocol flow by launching the CLI as a subprocess.
/// </summary>
/// <remarks>
/// These tests require network access to fetch documentation from aspire.dev.
/// They are marked as outerloop tests to avoid slowing down regular CI.
/// </remarks>
[OuterloopTest("Requires network access to fetch aspire.dev documentation")]
public partial class McpDocsE2ETests(McpDocsE2ETests.CliInstallFixture cli) : IAsyncLifetime, IClassFixture<McpDocsE2ETests.CliInstallFixture>
{
    private McpClient? _mcpClient;

    public async ValueTask InitializeAsync()
    {
        var options = new StdioClientTransportOptions
        {
            Name = "aspire-mcp-e2e-test",
            Command = cli.AspireExecutablePath,
            Arguments = ["agent", "mcp"]
        };

        var transport = new StdioClientTransport(options);
        _mcpClient = await McpClient.CreateAsync(transport);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListTools_IncludesDocsTools()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        Assert.Contains(tools, t => t.Name == "list_docs");
        Assert.Contains(tools, t => t.Name == "search_docs");
        Assert.Contains(tools, t => t.Name == "get_doc");
    }

    [Fact]
    public async Task ListDocs_ReturnsDocumentation()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        Assert.Contains("Aspire Documentation Pages", text);
        Assert.Contains("Slug:", text);
    }

    [Fact]
    public async Task SearchDocs_FindsRelevantContent()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "redis" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        // Should find Redis-related documentation
        Assert.Contains("Search Results", text);
        // Should include slug for use with get_doc
        Assert.Contains("Slug:", text);
    }

    [Fact]
    public async Task SearchDocs_RespectsTopKParameter()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "aspire", ["topK"] = 3 },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        // Should contain search results but limited count
        Assert.Contains("Search Results", text);
    }

    [Fact]
    public async Task SearchDocs_SlugCanBeUsedWithGetDoc()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // Search for documentation
        var searchResult = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "redis" },
            cancellationToken: cancellationToken);

        Assert.NotNull(searchResult);
        Assert.True(searchResult.IsError is null or false, $"Tool returned error: {GetResultText(searchResult)}");

        var searchText = GetResultText(searchResult);

        // Extract a slug from the search results
        var slugMatch = SlugRegex().Match(searchText);
        Assert.True(slugMatch.Success, "Could not find a slug in search_docs output. Search results should include slugs for use with get_doc.");

        var slug = slugMatch.Groups[1].Value;

        // Use the slug with get_doc to retrieve full content
        var docResult = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = slug },
            cancellationToken: cancellationToken);

        Assert.NotNull(docResult);
        Assert.True(docResult.IsError is null or false, $"get_doc returned error for slug '{slug}': {GetResultText(docResult)}");

        var docText = GetResultText(docResult);
        Assert.NotEmpty(docText);
    }

    [Fact]
    public async Task SearchDocs_WithEmptyQuery_ReturnsError()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is true, "Expected an error response for empty query");
    }

    [Fact]
    public async Task GetDoc_RetrievesDocumentContent()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // First list docs to get a valid slug
        var listResult = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);
        Assert.True(listResult.IsError is null or false);

        var listText = GetResultText(listResult);

        // Extract a slug from the list
        // Format: **Slug:** `some-slug`
        var slugMatch = SlugRegex().Match(listText);

        Assert.True(slugMatch.Success, "Could not find a slug in list_docs output");

        var slug = slugMatch.Groups[1].Value;

        // Now get that specific document
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = slug },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task GetDoc_WithInvalidSlug_ReturnsError()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = "nonexistent-doc-that-does-not-exist" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is true, "Expected an error response for invalid slug");
        Assert.Contains("No documentation found", GetResultText(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDoc_WithSection_ReturnsSpecificSection()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // First list docs to get a valid slug
        var listResult = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);
        Assert.True(listResult.IsError is null or false);

        var listText = GetResultText(listResult);

        // Extract a slug from the list
        var slugMatch = SlugRegex().Match(listText);

        Assert.True(slugMatch.Success, "Could not find a slug in list_docs output");

        var slug = slugMatch.Groups[1].Value;

        // Get document with a section filter (use a common section name)
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?>
            {
                ["slug"] = slug,
                ["section"] = "Configuration"  // Common section name in docs
            },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        // Even if section doesn't exist, should still return content
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
    }

    [Fact]
    public async Task ListTools_ToolSchemas_AreValid()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        var docTools = tools.Where(t => t.Name is "list_docs" or "search_docs" or "get_doc").ToList();

        foreach (var tool in docTools)
        {
            // Verify schema is valid JSON
            var schemaString = tool.ProtocolTool.InputSchema.ToString();
            Assert.NotEmpty(schemaString);

            // Should be parseable JSON
            using var parsed = JsonDocument.Parse(schemaString);
            Assert.NotNull(parsed);
        }
    }

    [Fact]
    public async Task SearchDocs_ToolDescription_IsInformative()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        var searchTool = tools.FirstOrDefault(t => t.Name == "search_docs");
        Assert.NotNull(searchTool);
        Assert.NotEmpty(searchTool.Description);
        Assert.Contains("search", searchTool.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetResultText(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
        {
            return string.Empty;
        }

        return result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? string.Empty;
    }

    [GeneratedRegex(@"\*\*Slug:\*\* `([^`]+)`")]
    private static partial Regex SlugRegex();

    /// <summary>
    /// Installs the Aspire CLI selected by <see cref="CliInstallStrategy.Detect"/> once for all tests in the class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests talk to <c>aspire agent mcp</c> over stdio from the host, so they can't use the Docker-based install
    /// helpers. The CLI is installed into an isolated temporary prefix (rather than <c>~/.aspire</c>) with the same
    /// acquisition scripts those helpers use. On CI, <c>ASPIRE_E2E_CLI_ARCHIVE_DIR</c> selects
    /// <see cref="CliInstallMode.LocalArchive"/>, so the tests exercise the packaged native CLI built for the commit.
    /// </para>
    /// <para>
    /// Installing up front also keeps acquisition out of <c>McpClient.CreateAsync</c>, whose initialization timeout
    /// should only cover the server's MCP handshake.
    /// </para>
    /// </remarks>
    public sealed class CliInstallFixture : IAsyncLifetime
    {
        private static readonly TimeSpan s_installTimeout = TimeSpan.FromMinutes(5);

        private DirectoryInfo? _installPrefix;

        public string AspireExecutablePath { get; private set; } = string.Empty;

        public async ValueTask InitializeAsync()
        {
            var strategy = CliInstallStrategy.Detect();
            var scriptsDir = Path.Combine(CliE2ETestHelpers.GetRepoRoot(), "eng", "scripts");

            if (strategy.Mode is CliInstallMode.Preinstalled)
            {
                AspireExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "bin", "aspire");
            }
            else
            {
                _installPrefix = Directory.CreateTempSubdirectory("aspire-mcp-e2e-cli-");
                var installPrefix = _installPrefix.FullName;
                var quotedPrefix = AspireCliShellCommandHelpers.QuoteBashArg(installPrefix);
                var prScript = $"bash {AspireCliShellCommandHelpers.QuoteBashArg(Path.Combine(scriptsDir, "get-aspire-cli-pr.sh"))}";
                var installScript = $"bash {AspireCliShellCommandHelpers.QuoteBashArg(Path.Combine(scriptsDir, "get-aspire-cli.sh"))}";

                // Keep the install self-contained: no PATH/profile edits, shell completions or VS Code extension.
                const string isolatedInstallArgs = "--skip-path --skip-completions";
                const string isolatedPrInstallArgs = $"--skip-extension {isolatedInstallArgs}";

                (string command, string cliBinDir) = strategy.Mode switch
                {
                    CliInstallMode.LocalArchive => (
                        $"{AspireCliShellCommandHelpers.GetLocalArchiveInstallCommand(strategy.ArchiveDir!, prScript)} --install-path {quotedPrefix} {isolatedPrInstallArgs}",
                        Path.Combine(installPrefix, "bin")),
                    CliInstallMode.PullRequest => GetPullRequestInstall(CliE2ETestHelpers.GetRequiredPrNumber()),
                    CliInstallMode.InstallScript => (
                        $"{AspireCliShellCommandHelpers.GetInstallScriptCommand(strategy, installScript)} --install-path {AspireCliShellCommandHelpers.QuoteBashArg(Path.Combine(installPrefix, "bin"))} {isolatedInstallArgs}",
                        Path.Combine(installPrefix, "bin")),
                    _ => throw new InvalidOperationException(
                        $"CLI install mode '{strategy}' is not supported by {nameof(McpDocsE2ETests)}, which launches the CLI on the host. " +
                        $"Set {CliInstallStrategy.CliArchiveDirEnvironmentVariableName} to a directory containing the CLI archive to test a local build.")
                };

                // get-aspire-cli-pr.sh isolates PR installs under <prefix>/dogfood/pr-<N>/bin.
                (string, string) GetPullRequestInstall(int prNumber) => (
                    $"{AspireCliShellCommandHelpers.GetPullRequestInstallCommand(prNumber, prScript)} --install-path {quotedPrefix} {isolatedPrInstallArgs}",
                    Path.Combine(installPrefix, "dogfood", $"pr-{prNumber}", "bin"));

                await RunBashAsync(command);
                AspireExecutablePath = Path.Combine(cliBinDir, "aspire");
            }

            if (!File.Exists(AspireExecutablePath))
            {
                throw new InvalidOperationException($"The Aspire CLI was not found at '{AspireExecutablePath}' after installing with strategy '{strategy}'.");
            }

            await VerifyVersionAsync(strategy);
        }

        public ValueTask DisposeAsync()
        {
            _installPrefix?.Delete(recursive: true);
            return ValueTask.CompletedTask;
        }

        // Mirrors CliE2EAutomatorHelpers.VerifyAspireCliVersionAsync so a stale or wrong binary fails setup instead
        // of silently testing a different CLI. Running the CLI once here also keeps any first-run work out of the
        // MCP initialization window.
        private async Task VerifyVersionAsync(CliInstallStrategy strategy)
        {
            // Example output: "17.0.0-pr.21450.g14083e0b". Some builds append "+<commit>" build metadata, which is ignored.
            var installedVersion = (await RunProcessAsync(AspireExecutablePath, ["--version"])).Trim();
            var baseVersion = installedVersion.Split('+')[0];

            if (strategy.ExpectedVersion is not null)
            {
                Assert.True(
                    baseVersion == strategy.ExpectedVersion,
                    $"Aspire CLI version mismatch. Expected '{strategy.ExpectedVersion}' (from {strategy.Mode}) but got '{installedVersion}'.");
            }
            else if (CliE2ETestHelpers.IsRunningInCI && strategy.Mode is CliInstallMode.LocalArchive)
            {
                Assert.Fail(
                    "Running on CI with LocalArchive mode but could not extract expected CLI version " +
                    $"from Aspire.Cli.*.nupkg in the archive directory ({strategy.ArchiveDir}).");
            }
        }

        private static Task<string> RunBashAsync(string command) => RunProcessAsync("bash", ["-c", command]);

        private static async Task<string> RunProcessAsync(string fileName, string[] arguments)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

            // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeoutCts.CancelAfter(s_installTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}.{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
            }

            return await stdout;
        }
    }
}

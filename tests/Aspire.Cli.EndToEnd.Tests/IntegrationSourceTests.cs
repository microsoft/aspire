// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class IntegrationSourceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [CaptureWorkspaceOnFailure]
    public async Task Add_FromLocalIntegrationSourcePersistsNuGetConfiguration(string language)
    {
        const string packageId = "Contoso.Hosting.LocalSource";
        const string packageVersion = "1.0.0";

        var cancellationToken = TestContext.Current.CancellationToken;
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            "Integration sources require a current source build. Run with ASPIRE_E2E_ARCHIVE or in pull request CI.");

        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.WorkspaceRoot.FullName;
        var feed = workspace.CreateDirectory("integration-feed");
        var containerFeed = CliE2ETestHelpers.ToContainerPath(feed.FullName, workspace);
        // This package exists only in the configured feed. A private, initially empty package
        // cache ensures a successful add cannot be satisfied by an earlier restore.
        using (var archive = ZipFile.Open(Path.Combine(feed.FullName, $"{packageId}.{packageVersion}.nupkg"), ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(archive.CreateEntry($"{packageId}.nuspec").Open()))
            {
                await writer.WriteAsync($"""
                    <?xml version="1.0"?>
                    <package>
                      <metadata>
                        <id>{packageId}</id>
                        <version>{packageVersion}</version>
                        <authors>Aspire Tests</authors>
                        <description>Local integration source fixture.</description>
                        <tags>aspire integration hosting polyglot</tags>
                      </metadata>
                    </package>
                    """);
            }
            archive.CreateEntry("lib/net10.0/_._");
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, cancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export NUGET_PACKAGES=\"$ASPIRE_E2E_WORKSPACE/.nuget-packages\"", counter);
        var template = language == "csharp" ? "aspire-empty" : "aspire-ts-empty";
        await auto.RunCommandAsync(
            $"aspire new {template} --language {language} --name SourceApp --output SourceApp " +
            "--non-interactive --suppress-agent-init --localhost-tld false",
            counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("cd SourceApp", counter);
        await auto.RunCommandAsync("aspire config set integrationSources.default.enabled false", counter);
        await auto.RunCommandAsync(
            "aspire config set integrationSources.local.source \"$ASPIRE_E2E_WORKSPACE/integration-feed\"", counter);

        var projectDirectory = Path.Combine(root, "SourceApp");
        var nugetConfigPath = Path.Combine(projectDirectory, "nuget.config");
        var originalConfig = File.Exists(nugetConfigPath) ? XDocument.Load(nugetConfigPath) : null;
        Assert.Empty(originalConfig?.Descendants("packageSources").Elements("add")
            .Where(source => (string?)source.Attribute("value") == containerFeed) ?? []);
        var packageCacheDirectory = Path.Combine(root, ".nuget-packages", packageId.ToLowerInvariant(), packageVersion);
        Assert.False(Directory.Exists(packageCacheDirectory));

        // Do not pass --source: discovery and installation must use integrationSources.
        await auto.RunCommandAsync(
            $"aspire add {packageId} --version {packageVersion} --non-interactive",
            counter, TimeSpan.FromMinutes(3));

        if (language == "csharp")
        {
            // File-based AppHosts record references as "#:package Package.Id@1.0.0".
            var packageDirective = Assert.Single(
                await File.ReadAllLinesAsync(Path.Combine(projectDirectory, "apphost.cs"), cancellationToken),
                line => line.StartsWith($"#:package {packageId}@", StringComparison.Ordinal));
            Assert.Equal($"#:package {packageId}@{packageVersion}", packageDirective);
            await auto.RunCommandAsync("dotnet build apphost.cs", counter, TimeSpan.FromMinutes(3));
        }
        else
        {
            var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectDirectory, "aspire.config.json"), cancellationToken))!;
            Assert.Equal(packageVersion, config["packages"]![packageId]!.GetValue<string>());
            await auto.RunCommandAsync("npm run build", counter, TimeSpan.FromMinutes(3));
        }

        var updatedConfig = XDocument.Load(nugetConfigPath);
        var addedSource = Assert.Single(updatedConfig.Descendants("packageSources").Elements("add"),
            source => (string?)source.Attribute("value") == containerFeed);
        var sourceKey = addedSource.Attribute("key")!.Value;
        var mapping = Assert.Single(updatedConfig.Descendants("packageSourceMapping").Elements("packageSource"),
            source => (string?)source.Attribute("key") == sourceKey);
        Assert.Equal([packageId], mapping.Elements("package").Select(package => (string?)package.Attribute("pattern")));

        foreach (var originalSource in originalConfig?.Descendants("packageSources").Elements("add") ?? [])
        {
            Assert.Contains(updatedConfig.Descendants("packageSources").Elements("add"),
                source => XNode.DeepEquals(originalSource, source));
        }
        foreach (var originalMapping in originalConfig?.Descendants("packageSourceMapping").Elements("packageSource") ?? [])
        {
            Assert.Contains(updatedConfig.Descendants("packageSourceMapping").Elements("packageSource"),
                source => XNode.DeepEquals(originalMapping, source));
        }

        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(packageCacheDirectory, ".nupkg.metadata"), cancellationToken))!;
        Assert.Equal(containerFeed, metadata["source"]!.GetValue<string>().TrimEnd('/'));
    }
}

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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void MappedConfigurationIncludesPublicDependencySource(bool hasConfiguration, bool hasHiveSource, bool hasPublicSource)
    {
        var originalConfig = hasConfiguration ? new XDocument(new XElement("configuration")) : null;
        if (hasHiveSource || hasPublicSource)
        {
            var sources = new XElement("packageSources");
            if (hasHiveSource)
            {
                sources.Add(new XElement("add", new XAttribute("key", "aspire-apphost-test"),
                    new XAttribute("value", "https://example.test/cli/v3/index.json")));
            }
            if (hasPublicSource)
            {
                sources.Add(new XElement("add", new XAttribute("key", "nuget.org"),
                    new XAttribute("value", "https://api.nuget.org/v3/index.json")));
            }
            originalConfig!.Root!.Add(sources);
        }

        var configuration = CreateMappedNuGetConfiguration(originalConfig);

        string[] expectedKeys = hasHiveSource ? ["aspire-apphost-test", "nuget.org"] : ["nuget.org"];
        var configuredSources = configuration.Descendants("packageSources").Elements("add").ToArray();
        Assert.Equal(expectedKeys, configuredSources.Select(source => (string?)source.Attribute("key")));
        var publicSource = Assert.Single(configuredSources, source => (string?)source.Attribute("key") == "nuget.org");
        Assert.Equal("https://api.nuget.org/v3/index.json", (string?)publicSource.Attribute("value"));
        var mappings = Assert.Single(configuration.Descendants("packageSourceMapping"));
        Assert.Single(mappings.Elements("clear"));
        Assert.Equal(expectedKeys, mappings.Elements("packageSource").Select(source => (string?)source.Attribute("key")));
        Assert.All(mappings.Elements("packageSource"), source =>
            Assert.Equal(["*"], source.Elements("package").Select(package => (string?)package.Attribute("pattern"))));
    }

    [Theory]
    [InlineData("csharp", false)]
    [InlineData("csharp", true)]
    [InlineData("typescript", false)]
    [InlineData("typescript", true)]
    [CaptureWorkspaceOnFailure]
    public async Task Add_FromLocalIntegrationSourcePersistsNuGetConfiguration(string language, bool hasMappings)
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
        if (hasMappings)
        {
            originalConfig = CreateMappedNuGetConfiguration(originalConfig);
            originalConfig.Save(nugetConfigPath);
        }
        Assert.Equal(hasMappings, originalConfig?.Descendants("packageSourceMapping").Elements("packageSource").Any() ?? false);
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
        var mappings = updatedConfig.Descendants("packageSourceMapping").Elements("packageSource");
        if (hasMappings)
        {
            var mapping = Assert.Single(mappings, source => (string?)source.Attribute("key") == sourceKey);
            Assert.Equal([packageId], mapping.Elements("package").Select(package => (string?)package.Attribute("pattern")));
        }
        else
        {
            Assert.Empty(mappings);
        }

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

    private static XDocument CreateMappedNuGetConfiguration(XDocument? originalConfig)
    {
        originalConfig ??= new XDocument(new XElement("configuration"));
        var sources = originalConfig.Root!.Element("packageSources");
        if (sources is null)
        {
            sources = new XElement("packageSources");
            originalConfig.Root.Add(sources);
        }
        // C# scaffolding can declare only its hive and inherit nuget.org. Declare
        // the public dependency source locally before replacing inherited mappings.
        if (!sources.Elements("add").Any(source =>
            string.Equals((string?)source.Attribute("key"), "nuget.org", StringComparison.OrdinalIgnoreCase)))
        {
            sources.Add(new XElement("add", new XAttribute("key", "nuget.org"),
                new XAttribute("value", "https://api.nuget.org/v3/index.json")));
        }
        originalConfig.Root!.Element("packageSourceMapping")?.Remove();
        originalConfig.Root.Add(new XElement("packageSourceMapping",
            new XElement("clear"),
            sources.Elements("add").Select(source =>
                new XElement("packageSource", new XAttribute("key", source.Attribute("key")!.Value),
                    new XElement("package", new XAttribute("pattern", "*"))))));

        return originalConfig;
    }
}

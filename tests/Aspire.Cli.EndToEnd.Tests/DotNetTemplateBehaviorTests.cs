// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class DotNetTemplateLocalhostTldTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, string, bool> LocalhostTldHostnameTestData()
    {
        return new()
        {
            { "aspire", "my.namespace.app", "my-namespace-app", true },
            { "aspire", ".StartWithDot", "startwithdot", true },
            { "aspire", "EndWithDot.", "endwithdot", true },
            { "aspire", "My..Test__Project", "my-test-project", true },
            { "aspire", "Project123.Test456", "project123-test456", true },
            { "aspire-apphost", "my.service.name", "my-service-name", true },
            { "aspire-apphost-singlefile", "-my.service..name-", "my-service-name", true },
            { "aspire-starter", "Test_App.1", "test-app-1", false },
            { "aspire-ts-cs-starter", "My-App.Test", "my-app-test", false }
        };
    }

    [Theory]
    [MemberData(nameof(LocalhostTldHostnameTestData))]
    [CaptureWorkspaceOnFailure]
    public async Task LocalhostTldGeneratesDnsCompliantHostnames(string templateName, string projectName, string expectedHostname, bool useBootstrapInstall)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        string projectRoot;
        if (useBootstrapInstall)
        {
            await DotNetTemplateBehaviorTestHelpers.CreateTemplateBootstrapAsync(auto, counter);
            projectRoot = await DotNetTemplateBehaviorTestHelpers.CreateDotNetTemplateInBootstrapAsync(auto, counter, workspace, templateName, projectName, ["--localhost-tld"]);
        }
        else
        {
            projectRoot = await DotNetTemplateBehaviorTestHelpers.CreateCliTemplateAsync(auto, counter, workspace, templateName, projectName, useLocalhostTld: true);
        }

        DotNetTemplateBehaviorTestHelpers.AssertDevLocalhostHostname(projectRoot, templateName, projectName, expectedHostname);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

public sealed class DotNetSingleFileAppHostTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task SingleFileAppHostTemplateBuildsAndStarts()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await DotNetTemplateBehaviorTestHelpers.CreateTemplateBootstrapAsync(auto, counter);
        await DotNetTemplateBehaviorTestHelpers.CreateDotNetTemplateInBootstrapAsync(auto, counter, workspace, "aspire-apphost-singlefile", "SingleFileAppHost", []);

        await auto.TypeAsync("cd SingleFileAppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("dotnet build apphost.cs");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

public sealed class DotNetTemplateTransportTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task NoHttpsTemplateRequiresAllowUnsecuredTransport()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await DotNetTemplateBehaviorTestHelpers.CreateTemplateBootstrapAsync(auto, counter);
        var projectRoot = await DotNetTemplateBehaviorTestHelpers.CreateDotNetTemplateInBootstrapAsync(auto, counter, workspace, "aspire", "NoHttpsApp", ["--no-https"]);
        var appHostDirectory = Path.Combine(projectRoot, "NoHttpsApp.AppHost");

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(appHostDirectory, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromMinutes(1));

        var detachLogPath = await auto.CaptureLatestAspireLogAsync(
            "~/.aspire/logs/cli_*_detach-child_*.log",
            workspace,
            counter,
            "_aspire-detach.log");
        Assert.Contains("must be an https address unless the 'ASPIRE_ALLOW_UNSECURED_TRANSPORT' environment variable is set to true", File.ReadAllText(detachLogPath), StringComparison.Ordinal);

        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync("export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

public sealed class DotNetTemplateTargetFrameworkTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DotNetTemplateCreatesSupportedTargetFrameworksAndOlderSdkRejectsNewerTarget()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await DotNetTemplateBehaviorTestHelpers.CreateTemplateBootstrapAsync(auto, counter);

        var projectRoots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["net8.0"] = await DotNetTemplateBehaviorTestHelpers.CreateDotNetTemplateInBootstrapAsync(auto, counter, workspace, "aspire", "EmptyNet8", ["-f", "net8.0"]),
            ["net10.0"] = await DotNetTemplateBehaviorTestHelpers.CreateDotNetTemplateInBootstrapAsync(auto, counter, workspace, "aspire", "EmptyNet10", ["-f", "net10.0"])
        };

        foreach (var (targetFramework, projectRoot) in projectRoots)
        {
            DotNetTemplateBehaviorTestHelpers.AssertGeneratedProjectsTargetFramework(projectRoot, targetFramework);

            await auto.TypeAsync($"dotnet build {AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(projectRoot, workspace))}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));
        }

        await auto.TypeAsync(
            "if [ ! -x /tmp/dotnet8/dotnet ]; then " +
            "curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && " +
            "bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /tmp/dotnet8 --no-path; " +
            "fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(5));

        var olderSdkBuildLogPath = CliE2ETestHelpers.GetWorkspaceFilePath(workspace, "dotnet8-net10-build.log");
        var quotedOlderSdkBuildLogPath = AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(olderSdkBuildLogPath, workspace));
        CliE2ETestHelpers.RegisterCaptureFile("dotnet8-net10-build.log", olderSdkBuildLogPath);

        await auto.TypeAsync(
            $"DOTNET_MULTILEVEL_LOOKUP=0 /tmp/dotnet8/dotnet build {AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(projectRoots["net10.0"], workspace))} > {quotedOlderSdkBuildLogPath} 2>&1");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromMinutes(2));

        Assert.Contains("The current .NET SDK does not support targeting .NET 10.0", File.ReadAllText(olderSdkBuildLogPath), StringComparison.Ordinal);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

public sealed class DotNetTemplateProjectFileBehaviorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [CaptureWorkspaceOnFailure]
    public async Task DotNetTemplateWithExplicitSdkReferenceBuildsAndStarts(bool includeAspireHostingAppHostPackageReference)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableShowAllTemplatesAsync(counter);

        await auto.AspireNewSubcommandAsync("aspire", "ExplicitSdkApp", counter);

        var appHostProjectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "ExplicitSdkApp", "ExplicitSdkApp.AppHost", "ExplicitSdkApp.AppHost.csproj");
        DotNetTemplateBehaviorTestHelpers.RewriteAsExplicitSdkReference(appHostProjectPath, includeAspireHostingAppHostPackageReference);

        var appHostDirectory = Path.GetDirectoryName(appHostProjectPath)!;
        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(appHostDirectory, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("dotnet build");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Theory]
    [InlineData("8.1.0")]
    [InlineData("9.*-*")]
    [InlineData("[9.0.0]")]
    [CaptureWorkspaceOnFailure]
    public async Task DotNetAppHostTemplateBuildsWithAppHostPackageVersionOverride(string version)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableShowAllTemplatesAsync(counter);

        await auto.AspireNewSubcommandAsync("aspire-apphost", "VersionedAppHost", counter);

        var appHostRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "VersionedAppHost");
        var appHostProjectPath = Path.Combine(appHostRoot, "VersionedAppHost.csproj");
        var nugetConfigPath = Path.Combine(appHostRoot, "nuget.config");
        if (File.Exists(nugetConfigPath))
        {
            DotNetTemplateBehaviorTestHelpers.DisableAspirePackageSourceMapping(nugetConfigPath);
        }

        DotNetTemplateBehaviorTestHelpers.AddAspireHostingAppHostPackageReference(appHostProjectPath, version);

        await auto.TypeAsync("cd VersionedAppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("dotnet build");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DotNetTemplateWithCentralPackageManagementBuildsAndStarts()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableShowAllTemplatesAsync(counter);

        await auto.AspireNewSubcommandAsync("aspire", "CpmApp", counter);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "CpmApp");
        var appHostProjectPath = Path.Combine(projectRoot, "CpmApp.AppHost", "CpmApp.AppHost.csproj");
        DotNetTemplateBehaviorTestHelpers.AddCentralPackageManagementForRedis(appHostProjectPath, Path.Combine(projectRoot, "Directory.Packages.props"));

        await auto.TypeAsync("cd CpmApp/CpmApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("dotnet build");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

internal static partial class DotNetTemplateBehaviorTestHelpers
{
    internal static async Task CreateTemplateBootstrapAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        await auto.AspireNewSubcommandAsync("aspire-starter", "TemplateBootstrap", counter, "--use-redis-cache", "false", "--test-framework", "None");
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("cd TemplateBootstrap");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    internal static async Task<string> CreateDotNetTemplateInBootstrapAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string templateName,
        string projectName,
        IReadOnlyList<string> extraArgs)
    {
        var commandParts = new List<string>
        {
            "dotnet",
            "new",
            templateName,
            "-n",
            Quote(projectName),
            "-o",
            Quote($"./{projectName}")
        };

        commandParts.AddRange(extraArgs);

        await auto.TypeAsync(string.Join(" ", commandParts));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        var markerFileName = templateName switch
        {
            "aspire" or "aspire-starter" or "aspire-ts-cs-starter" => "*.sln",
            "aspire-apphost-singlefile" => "apphost.run.json",
            _ => null
        };

        return markerFileName is null
            ? Path.Combine(GetTemplateBootstrapRoot(workspace), projectName)
            : ResolveGeneratedTemplateDirectory(workspace, projectName, markerFileName);
    }

    internal static string ResolveGeneratedTemplateDirectory(TemporaryWorkspace workspace, string projectName, string markerFileName)
    {
        var bootstrapRoot = GetTemplateBootstrapRoot(workspace);
        var expectedProjectRoot = Path.Combine(bootstrapRoot, projectName);
        if (Directory.Exists(expectedProjectRoot))
        {
            return expectedProjectRoot;
        }

        var candidates = Directory.EnumerateFiles(bootstrapRoot, markerFileName, SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .Where(directory =>
            {
                var relativePath = Path.GetRelativePath(bootstrapRoot, directory);

                return relativePath != "." &&
                    !relativePath.StartsWith("TemplateBootstrap", StringComparison.Ordinal);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return Assert.Single(candidates);
    }

    private static string GetTemplateBootstrapRoot(TemporaryWorkspace workspace)
        => Path.Combine(workspace.WorkspaceRoot.FullName, "TemplateBootstrap");

    internal static async Task<string> CreateCliTemplateAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string templateName,
        string projectName,
        bool useLocalhostTld)
    {
        var extraArgs = templateName switch
        {
            "aspire-starter" => useLocalhostTld
                ? new[] { "--localhost-tld", "--use-redis-cache", "false", "--test-framework", "None" }
                : new[] { "--use-redis-cache", "false", "--test-framework", "None" },
            "aspire-ts-cs-starter" => useLocalhostTld
                ? new[] { "--localhost-tld", "--use-redis-cache", "false" }
                : new[] { "--use-redis-cache", "false" },
            _ => useLocalhostTld ? ["--localhost-tld"] : []
        };

        await auto.AspireNewSubcommandAsync(templateName, projectName, counter, extraArgs);
        return Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
    }

    internal static void AssertDevLocalhostHostname(string projectRoot, string templateName, string projectName, string expectedHostname)
    {
        var settingsPath = templateName switch
        {
            "aspire" or "aspire-starter" or "aspire-ts-cs-starter" => Path.Combine(projectRoot, $"{projectName}.AppHost", "Properties", "launchSettings.json"),
            "aspire-apphost" => Path.Combine(projectRoot, "Properties", "launchSettings.json"),
            "aspire-apphost-singlefile" => Path.Combine(projectRoot, "apphost.run.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(templateName), templateName, "Unknown template name.")
        };

        if (!File.Exists(settingsPath))
        {
            settingsPath = FindGeneratedAppHostSettingsPath(projectRoot, templateName);
        }

        Assert.True(File.Exists(settingsPath), $"Expected launch settings at {settingsPath}.");

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var profiles = document.RootElement.GetProperty("profiles");

        var foundDevLocalhost = false;
        foreach (var profile in profiles.EnumerateObject())
        {
            if (!profile.Value.TryGetProperty("applicationUrl", out var applicationUrl))
            {
                continue;
            }

            var urls = applicationUrl.GetString();
            if (string.IsNullOrEmpty(urls) || !urls.Contains(".dev.localhost:", StringComparison.Ordinal))
            {
                continue;
            }

            foundDevLocalhost = true;
            Assert.Contains($"{expectedHostname}.dev.localhost:", urls, StringComparison.Ordinal);

            foreach (Match match in DevLocalhostHostnameRegex().Matches(urls))
            {
                var hostname = match.Groups[1].Value;
                Assert.DoesNotContain("_", hostname, StringComparison.Ordinal);
                Assert.DoesNotContain(".", hostname, StringComparison.Ordinal);
                Assert.False(hostname.StartsWith("-", StringComparison.Ordinal), $"Hostname '{hostname}' should not start with hyphen.");
                Assert.False(hostname.EndsWith("-", StringComparison.Ordinal), $"Hostname '{hostname}' should not end with hyphen.");
            }
        }

        Assert.True(foundDevLocalhost, $"Expected a .dev.localhost URL in {settingsPath}.");
    }

    internal static void AssertGeneratedProjectsTargetFramework(string projectRoot, string expectedTargetFramework)
    {
        Assert.True(Directory.Exists(projectRoot), $"Expected project root at {projectRoot}.");

        var projectFiles = Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(projectFiles);

        foreach (var projectFile in projectFiles)
        {
            var document = XDocument.Load(projectFile);
            var targetFrameworks = document.Descendants()
                .Where(static element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(static element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();

            Assert.Contains(expectedTargetFramework, targetFrameworks);
        }
    }

    private static string FindGeneratedAppHostSettingsPath(string projectRoot, string templateName)
    {
        Assert.True(Directory.Exists(projectRoot), $"Expected project root at {projectRoot}.");

        if (templateName is "aspire-apphost-singlefile")
        {
            return Assert.Single(Directory.EnumerateFiles(projectRoot, "apphost.run.json", SearchOption.AllDirectories));
        }

        var candidates = Directory.EnumerateFiles(projectRoot, "launchSettings.json", SearchOption.AllDirectories)
            .Where(path =>
            {
                var propertiesDirectory = Directory.GetParent(path);
                var appHostDirectory = propertiesDirectory?.Parent;

                return string.Equals(propertiesDirectory?.Name, "Properties", StringComparison.Ordinal) &&
                    appHostDirectory?.Name.EndsWith(".AppHost", StringComparison.Ordinal) is true;
            })
            .ToArray();

        return Assert.Single(candidates);
    }

    internal static void RewriteAsExplicitSdkReference(string appHostProjectPath, bool includeAspireHostingAppHostPackageReference)
    {
        var document = XDocument.Load(appHostProjectPath);
        var project = document.Root ?? throw new InvalidOperationException($"Project root not found in {appHostProjectPath}.");
        var sdkAttribute = project.Attribute("Sdk") ?? throw new InvalidOperationException($"Sdk attribute not found in {appHostProjectPath}.");
        var version = ExtractSdkVersion(sdkAttribute.Value);

        project.SetAttributeValue("Sdk", "Microsoft.NET.Sdk");
        project.Elements("Sdk").Remove();
        project.AddFirst(new XElement("Sdk",
            new XAttribute("Name", "Aspire.AppHost.Sdk"),
            new XAttribute("Version", version)));

        if (includeAspireHostingAppHostPackageReference)
        {
            project.Add(new XElement("ItemGroup",
                new XElement("PackageReference",
                    new XAttribute("Include", "Aspire.Hosting.AppHost"),
                    new XAttribute("Version", version))));
        }

        document.Save(appHostProjectPath);
    }

    internal static void AddAspireHostingAppHostPackageReference(string appHostProjectPath, string version)
    {
        var document = XDocument.Load(appHostProjectPath);
        var project = document.Root ?? throw new InvalidOperationException($"Project root not found in {appHostProjectPath}.");
        project.Add(new XElement("ItemGroup",
            new XElement("PackageReference",
                new XAttribute("Include", "Aspire.Hosting.AppHost"),
                    new XAttribute("Version", version))));
        document.Save(appHostProjectPath);
    }

    internal static void DisableAspirePackageSourceMapping(string nugetConfigPath)
    {
        var document = XDocument.Load(nugetConfigPath);
        document.Root?.Element("packageSourceMapping")?.Remove();
        document.Save(nugetConfigPath);
    }

    internal static void AddCentralPackageManagementForRedis(string appHostProjectPath, string directoryPackagesPropsPath)
    {
        var document = XDocument.Load(appHostProjectPath);
        var project = document.Root ?? throw new InvalidOperationException($"Project root not found in {appHostProjectPath}.");
        var sdkAttribute = project.Attribute("Sdk") ?? throw new InvalidOperationException($"Sdk attribute not found in {appHostProjectPath}.");
        var version = ExtractSdkVersion(sdkAttribute.Value);

        project.Add(new XElement("ItemGroup",
            new XElement("PackageReference",
                new XAttribute("Include", "Aspire.Hosting.Redis"))));
        document.Save(appHostProjectPath);

        File.WriteAllText(directoryPackagesPropsPath, $$"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <NoWarn>NU1507;$(NoWarn)</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Aspire.Hosting.Redis" Version="{{version}}" />
              </ItemGroup>
            </Project>
            """);
    }

    private static string ExtractSdkVersion(string sdkValue)
    {
        const string prefix = "Aspire.AppHost.Sdk/";
        return sdkValue.StartsWith(prefix, StringComparison.Ordinal)
            ? sdkValue[prefix.Length..]
            : throw new InvalidOperationException($"Unexpected SDK value '{sdkValue}'.");
    }

    internal static string Quote(string value) => $"\"{value}\"";

    [GeneratedRegex(@"://([^:]+)\.dev\.localhost:")]
    private static partial Regex DevLocalhostHostnameRegex();
}

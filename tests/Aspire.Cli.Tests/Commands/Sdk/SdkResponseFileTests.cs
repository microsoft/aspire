// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Commands.Sdk;

public class SdkResponseFileTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("generate", false)]
    [InlineData("generate", true)]
    [InlineData("dump", false)]
    [InlineData("dump", true)]
    [InlineData("dump-directory", false)]
    [InlineData("dump-directory", true)]
    [InlineData("export", false)]
    [InlineData("export", true)]
    public async Task SdkCommands_IgnoreAutomaticResponseFiles(string commandName, bool parentDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);
        // MSBuild's child working directory resolves symlinks. Use that same path spelling
        // for project references, particularly /var -> /private/var on macOS.
        var modelDirectory = new DirectoryInfo(PathNormalizer.ResolveSymlinks(workspace.CreateDirectory("scanner").FullName));
        var responseFile = Path.Combine(
            parentDirectory ? workspace.Path : modelDirectory.FullName, "Directory.Build.rsp");
        const string unexpectedTarget = "ResponseFileMustNotBeLoaded";
        await File.WriteAllTextAsync(responseFile, $"-target:{unexpectedTarget}");

        var interaction = new TestInteractionService();
        var aspireHome = new DirectoryInfo(PathNormalizer.ResolveSymlinks(workspace.CreateDirectory("custom-aspire-home").FullName));
        // A scanner must not inherit the native-server setting or use this ancestor as
        // its integration-cache root, even though it lives underneath Aspire home.
        await File.WriteAllTextAsync(Path.Combine(aspireHome.FullName, "aspire.config.json"),
            """{"features":{"appHostServerV2":true}}""");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(aspireHomeDirectory: aspireHome);
            options.InteractionServiceFactory = _ => interaction;
            options.DotNetCliExecutionFactoryFactory = provider =>
                new ProcessExecutionFactory(new HostEnvironment(), provider.GetRequiredService<ILogger<ProcessExecutionFactory>>());
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
            };
        });
        services.AddSingleton<IEnvironment, HostEnvironment>();
        var factoryCalls = 0;
        string? scannerPath = null;
        services.AddSingleton<IAppHostServerProjectFactory>(provider =>
        {
            var realFactory = ActivatorUtilities.CreateInstance<AppHostServerProjectFactory>(provider);
            return new TestAppHostServerProjectFactory
            {
                // Inject the response file without replacing production scanner creation,
                // build, restore, server startup, or RPC.
                CreateAsyncCallback = async (appPath, cancellationToken) =>
                {
                    factoryCalls++;
                    scannerPath = appPath;
                    Assert.Equal(Path.Combine(aspireHome.FullName, "tmp", "sdk"), Directory.GetParent(appPath)?.FullName);
                    Assert.Equal(appPath, ConfigurationHelper.GetConfigRootDirectory(new DirectoryInfo(appPath)).FullName);
                    var project = Assert.IsType<DotNetBasedAppHostServerProject>(
                        await realFactory.CreateForSdkCommandsAsync(appPath, repoRoot, cancellationToken));
                    Assert.Equal(Path.Combine(appPath, "server"), project.ProjectModelPath);
                    await File.WriteAllTextAsync(
                        Path.Combine(parentDirectory ? appPath : project.ProjectModelPath, "Directory.Build.rsp"),
                        $"-target:{unexpectedTarget}", cancellationToken);
                    return project;
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        // Prove the response file would affect an ordinary build from this exact directory.
        var probeProject = new FileInfo(Path.Combine(modelDirectory.FullName, "Probe.proj"));
        await File.WriteAllTextAsync(probeProject.FullName, "<Project><Target Name=\"Build\" /></Project>");
        var probeOutput = new OutputCollector();
        var probeExitCode = await provider.GetRequiredService<IDotNetCliRunner>().BuildAsync(
            probeProject, noRestore: true, new ProcessInvocationOptions
            {
                StandardOutputCallback = probeOutput.AppendOutput,
                StandardErrorCallback = probeOutput.AppendError
            }, TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotEqual(0, probeExitCode);
        Assert.Contains(probeOutput.GetLines(), line => line.Line.Contains(unexpectedTarget, StringComparison.Ordinal));
        File.Delete(probeProject.FullName);

        var integrationProject = Path.Combine(repoRoot, "src", "Aspire.Hosting", "Aspire.Hosting.csproj");
        var outputPath = Path.Combine(workspace.Path, "output");
        string[] arguments = commandName switch
        {
            "generate" => ["sdk", "generate", integrationProject, "--language", "typescript", "--output", outputPath],
            "dump" => ["sdk", "dump", integrationProject, "--format", "json", "--output", outputPath],
            "dump-directory" => ["sdk", "dump", integrationProject, "--format", "ci", "--output-directory", outputPath],
            "export" => ["sdk", "export", "--language", "typescript"],
            _ => throw new ArgumentOutOfRangeException(nameof(commandName))
        };

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(arguments).InvokeAsync()
            .DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, factoryCalls);
        Assert.NotNull(scannerPath);
        Assert.False(Directory.Exists(scannerPath));
        Assert.Equal(["aspire.config.json", "tmp"],
            Directory.GetFileSystemEntries(aspireHome.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal($"-target:{unexpectedTarget}", await File.ReadAllTextAsync(responseFile));
        switch (commandName)
        {
            case "generate":
                Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(outputPath, "aspire.mts")));
                break;
            case "dump":
                using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath)))
                {
                    Assert.NotEmpty(document.RootElement.GetProperty("Capabilities").EnumerateArray());
                }
                break;
            case "export":
                var stdout = Assert.Single(interaction.DisplayedRawText,
                    entry => entry.ConsoleOverride == ConsoleOutput.Standard);
                using (var document = JsonDocument.Parse(stdout.Text))
                {
                    Assert.Equal("Aspire.Hosting", document.RootElement.GetProperty("package").GetProperty("name").GetString());
                    Assert.NotEmpty(document.RootElement.GetProperty("declarations").EnumerateArray());
                }
                break;
            case "dump-directory":
                Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(outputPath, "Aspire.Hosting.ats.txt")));
                break;
        }
    }
}

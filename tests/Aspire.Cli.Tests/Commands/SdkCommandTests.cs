// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class SdkCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("generate")]
    [InlineData("dump")]
    [InlineData("dump-directory")]
    [InlineData("export")]
    public async Task SdkCommands_UseUniqueAspireHomeDirectoriesAndCleanUpAfterBuildFailure(string commandName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var aspireHome = new DirectoryInfo(Path.Combine(workspace.Path, "custom-aspire-home"));
        aspireHome.Create();
        await File.WriteAllTextAsync(Path.Combine(aspireHome.FullName, "aspire.config.json"),
            """{"features":{"appHostServerV2":true}}""");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { Path.Combine(aspireHome.FullName, "tmp"), Path.Combine(aspireHome.FullName, "tmp", "sdk") })
            {
                Directory.CreateDirectory(path);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }
        }
        var scannerPaths = new List<string>();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(aspireHomeDirectory: aspireHome);
        });
        services.AddSingleton<IAppHostServerProjectFactory>(new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (appPath, _) =>
            {
                Assert.Equal(Path.Combine(aspireHome.FullName, "tmp", "sdk"), Directory.GetParent(appPath)?.FullName);
                Assert.True(Directory.Exists(appPath));
                Assert.Equal(appPath, ConfigurationHelper.GetConfigRootDirectory(new DirectoryInfo(appPath)).FullName);
                Assert.Equal(Path.Combine(appPath, ".aspire", "integrations"),
                    ConfigurationHelper.GetIntegrationCacheDirectory(new DirectoryInfo(appPath)).FullName);
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var directory in new[] { appPath, Path.Combine(aspireHome.FullName, "tmp"), Path.Combine(aspireHome.FullName, "tmp", "sdk") })
                    {
                        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            File.GetUnixFileMode(directory));
                    }
                }
                scannerPaths.Add(appPath);
                return Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(appPath));
            }
        });
        using var provider = services.BuildServiceProvider();
        var integrationProject = Path.Combine(workspace.Path, "Integration.csproj");
        await File.WriteAllTextAsync(integrationProject, "<Project />");
        var outputPath = Path.Combine(workspace.Path, "output");
        string[] arguments = commandName switch
        {
            "generate" => ["sdk", "generate", integrationProject, "--language", "typescript", "--output", outputPath],
            "dump" => ["sdk", "dump", "--output", outputPath],
            "dump-directory" => ["sdk", "dump", integrationProject, "--format", "ci", "--output-directory", outputPath],
            "export" => ["sdk", "export", "--language", "typescript"],
            _ => throw new ArgumentOutOfRangeException(nameof(commandName))
        };
        var command = provider.GetRequiredService<RootCommand>();

        for (var i = 0; i < 2; i++)
        {
            var exitCode = await command.Parse(arguments).InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);
            Assert.Equal(i + 1, scannerPaths.Count);
            Assert.False(Directory.Exists(scannerPaths[i]));
        }

        Assert.Equal(2, scannerPaths.Distinct().Count());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(aspireHome.FullName, "tmp", "sdk")));
    }

    [Fact]
    public async Task SdkCommand_WithoutSubcommand_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }
}

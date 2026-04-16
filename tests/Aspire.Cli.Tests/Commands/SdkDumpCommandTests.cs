// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Commands;

public class SdkDumpCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly RemoteInvokeOptions s_remoteInvokeOptions = new()
    {
        StartInfo = { RedirectStandardOutput = true }
    };

    [Fact]
    public async Task SdkDumpWithHelpReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    [InlineData("ci")]
    [InlineData("Ci")]
    [InlineData("pretty")]
    [InlineData("Pretty")]
    public void ParsesFormatOptionWithoutErrors(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"sdk dump --format {format}");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SdkDumpWithNonexistentCsprojReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump /nonexistent/path/to/integration.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithEmptyPackageNameReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump @13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithEmptyVersionReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithInvalidVersionFormatReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@not-a-version!!!");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithInvalidArgumentFormatReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump some-random-string");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void ParsesValidPackageFormatWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParsesMultipleMixedArgumentsWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0 Aspire.Hosting.PostgreSQL@13.2.0");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParsesPreReleaseVersionWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0-preview.1");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SdkDumpWithDoubleAtSignReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // LastIndexOf('@') splits as "Aspire.Hosting.Redis@" and "13.2.0"
        // The @ in the package name part triggers format validation
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@@13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // "Aspire.Hosting.Redis@" is not a valid semver, so it should fail version validation
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void SdkDumpCi_ForHostingProject_DoesNotEmitWarnings()
    {
        // Assertions and skips inside the callback are surfaced back to the parent test process.
        using var result = RemoteExecutor.Invoke(async (baseDirectory) =>
        {
            var repoRoot = TryFindRepoRoot(baseDirectory);
            if (repoRoot is null)
            {
                Assert.Skip($"Could not find Aspire.slnx starting from '{baseDirectory}'.");
                return;
            }

            var projectPath = Path.Combine(repoRoot, "src", "Aspire.Hosting", "Aspire.Hosting.csproj");
            Assert.True(File.Exists(projectPath), $"Could not find Aspire.Hosting project at '{projectPath}'.");

            var tempDirectory = Path.Combine(Path.GetTempPath(), "aspire-sdk-dump-tests", $"{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var outputPath = Path.Combine(tempDirectory, "capabilities.txt");

            try
            {
                Environment.CurrentDirectory = repoRoot;
                Environment.SetEnvironmentVariable("ASPIRE_REPO_ROOT", repoRoot);
                Environment.SetEnvironmentVariable(CliConfigNames.NoLogo, "true");
                Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "true");
                Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "true");
                Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");

                // ExtraLongTimeout because this spawns a real dotnet build of Aspire.Hosting.csproj
                // in a child process, which can exceed the default timeout under concurrent test load.
                var exitCode = await Program.Main(["sdk", "dump", "--format", "ci", "--output", outputPath, projectPath]).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
                Assert.Equal(ExitCodeConstants.Success, exitCode);

                var output = await File.ReadAllTextAsync(outputPath);
                Assert.NotEmpty(output);

                var warningLines = output
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("warning:", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                Console.WriteLine($"Warnings: {warningLines.Length}");
                foreach (var warningLine in warningLines)
                {
                    Console.WriteLine(warningLine);
                }

                Assert.Empty(warningLines);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }, AppContext.BaseDirectory, options: s_remoteInvokeOptions);

        outputHelper.WriteLine(result.Process.StandardOutput.ReadToEnd());
    }

    private static string? TryFindRepoRoot(string startPath)
    {
        var currentDirectory = new DirectoryInfo(startPath);

        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "Aspire.slnx")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }
}

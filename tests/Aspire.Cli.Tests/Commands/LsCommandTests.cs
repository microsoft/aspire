// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class LsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task LsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_WhenNoCandidates_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task LsCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"ls --format {format}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_ReturnsCandidateAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var candidateAppHosts = JsonSerializer.Deserialize(jsonOutput, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
        Assert.NotNull(candidateAppHosts);

        Assert.Collection(candidateAppHosts,
            first =>
            {
                Assert.Equal(Path.Combine("App1", "App1.AppHost.csproj"), first.RelativePath);
                Assert.Equal(appHostPath1, first.Path);
                Assert.Equal(KnownLanguageId.CSharp, first.Language);
            },
            second =>
            {
                Assert.Equal(Path.Combine("App2", "App2.AppHost.csproj"), second.RelativePath);
                Assert.Equal(appHostPath2, second.Path);
                Assert.Equal(KnownLanguageId.TypeScript, second.Language);
            });
    }

    [Fact]
    public async Task LsCommand_JsonFormat_WhenNoCandidates_ReturnsEmptyArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }
}

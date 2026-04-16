// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class ApiCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ApiCommand_WithNoSubcommand_ShowsHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ApiDocsIndexServiceFactory = _ => new TestApiDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs api");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task ApiListCommand_WithScope_ReturnsEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ApiDocsIndexServiceFactory = _ => new TestApiDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs api list csharp");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ApiSearchCommand_WithLanguageFilter_ReturnsResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ApiDocsIndexServiceFactory = _ => new TestApiDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs api search emulator --language typescript --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ApiGetCommand_WithValidId_ReturnsContent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ApiDocsIndexServiceFactory = _ => new TestApiDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs api get csharp/aspire.test.package/testtype");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ApiGetCommand_WithInvalidId_ReturnsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ApiDocsIndexServiceFactory = _ => new TestApiDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs api get missing/id");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }
}

internal sealed class TestApiDocsIndexService : IApiDocsIndexService
{
    public bool IsIndexed => true;

    public ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<ApiListItem>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ApiListItem> items =
        [
            new()
            {
                Id = "csharp/aspire.test.package",
                Name = "aspire.test.package",
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.Package,
                ParentId = "csharp"
            }
        ];

        return ValueTask.FromResult(items);
    }

    public ValueTask<IReadOnlyList<ApiSearchResult>> SearchAsync(string query, string? language = null, int topK = 10, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ApiSearchResult> items =
        [
            new()
            {
                Id = "typescript/aspire.hosting.test/testresource/runasemulator",
                Name = "runAsEmulator",
                Language = ApiReferenceLanguages.TypeScript,
                Kind = ApiReferenceKinds.Member,
                ParentId = "typescript/aspire.hosting.test/testresource",
                Summary = "Runs the emulator locally.",
                Score = 100
            }
        ];

        return ValueTask.FromResult(items);
    }

    public ValueTask<ApiContent?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (id == "csharp/aspire.test.package/testtype")
        {
            return ValueTask.FromResult<ApiContent?>(new ApiContent
            {
                Id = id,
                Name = "TestType",
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.Type,
                Url = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype",
                ParentId = "csharp/aspire.test.package",
                Content = "# TestType\n\nRepresents a test type."
            });
        }

        return ValueTask.FromResult<ApiContent?>(null);
    }
}

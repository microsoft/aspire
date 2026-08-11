// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aspire.Tests.Utils;
using Aspire.Hosting.Ats;
using Aspire.Hosting.Tests;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[Trait("Partition", "4")]
public class AtsExportsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void GetConnectionString_ReturnsConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:cache"] = "UseDevelopmentStorage=true"
            })
            .Build();

        var value = BuilderExports.GetConnectionString(configuration, "cache");

        Assert.Equal("UseDevelopmentStorage=true", value);
    }

    [Fact]
    public void IsDevelopment_ReturnsTrueForDevelopmentEnvironment()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = Environments.Development
        };

        var result = BuilderExports.IsDevelopment(environment);

        Assert.True(result);
    }

    [Fact]
    public void IsEnvironment_ReturnsTrueForMatchingEnvironmentName()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Custom"
        };

        var result = BuilderExports.IsEnvironment(environment, "Custom");

        Assert.True(result);
    }

    [Fact]
    public void ParseLogLevel_UnknownLevelDefaultsToInformation()
    {
        var result = LoggingExports.ParseLogLevel("verbose");

        Assert.Equal(LogLevel.Information, result);
    }

    [Fact]
    public void ParseLogLevel_StrictModeThrowsForUnknownLevel()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => LoggingExports.ParseLogLevel("verbose", throwOnUnknown: true));

        Assert.Equal("level", exception.ParamName);
    }

    // Polyglot callers cannot mutate the input handles they pass to PromptInputs (those handles live on the
    // server and only the data fields cross the wire), so submitted values must travel back through the result.
    // This round-trips a prompt to prove the result carries the values keyed by input name.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
    [Fact]
    public async Task PromptInputs_ResultCarriesSubmittedValuesByName()
    {
        var interactionService = new TestInteractionService();

        var promptTask = InteractionExports.PromptInputs(
            interactionService,
            "Configure",
            "Fill out the form.",
            [
                InteractionExports.CreateTextInput(interactionService, "region"),
                InteractionExports.CreateTextInput(interactionService, "zone"),
            ]);

        var data = await interactionService.Interactions.Reader.ReadAsync();
        data.Inputs["region"].Value = "westus";
        data.Inputs["zone"].Value = "a";
        data.CompletionTcs.SetResult(InteractionResult.Ok(data.Inputs));

        var result = await promptTask;

        Assert.False(result.Canceled);
        // Inputs is now the InteractionInputCollection handle, so callers can read submitted values by name
        // (mirroring the .NET indexer) in addition to enumerating them.
        Assert.Equal("westus", result.Inputs["region"].Value);
        Assert.Equal("a", result.Inputs["zone"].Value);
        Assert.Collection(
            result.Inputs,
            input =>
            {
                Assert.Equal("region", input.Name);
                Assert.Equal("westus", input.Value);
            },
            input =>
            {
                Assert.Equal("zone", input.Name);
                Assert.Equal("a", input.Value);
            });
    }

    [Fact]
    public async Task PromptInputs_ResultCarriesSubmittedFileMetadataByName()
    {
        var interactionService = new TestInteractionService();

        var promptTask = InteractionExports.PromptInputs(
            interactionService,
            "Upload",
            "Select a file.",
            [
                InteractionExports.CreateFileInput(interactionService, "artifact", new CreateInteractionInputOptions
                {
                    Label = "Artifact",
                    MaxFileSize = 1024
                }),
            ]);

        var data = await interactionService.Interactions.Reader.ReadAsync();
        data.Inputs["artifact"].Value = "/repo/artifact.zip";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.WorkspaceRoot.FullName, "artifact.zip");
        await File.WriteAllTextAsync(tempFile, "test content");
        data.Inputs["artifact"].SetFiles([new InteractionFile("file-1", "artifact.zip", tempFile)]);

        data.CompletionTcs.SetResult(InteractionResult.Ok(data.Inputs));

        var result = await promptTask;

        Assert.False(result.Canceled);
        Assert.Equal("/repo/artifact.zip", result.Inputs["artifact"].Value);
        Assert.NotNull(result.Inputs["artifact"].Files);
        var file = Assert.Single(result.Inputs["artifact"].Files!);
        Assert.Equal("artifact.zip", file.Name);
        Assert.Equal(1024, result.Inputs["artifact"].MaxFileSize);
    }

    [Fact]
    public void CreateFileInput_CreatesFileWithOptions()
    {
        var interactionService = new TestInteractionService();

        var input = InteractionExports.CreateFileInput(interactionService, "artifact", new CreateInteractionInputOptions
        {
            Label = "Artifact",
            Placeholder = "Choose artifact",
            MaxFileSize = 2048
        }).Input;

        Assert.Equal("artifact", input.Name);
        Assert.Equal("Artifact", input.Label);
        Assert.Equal("Choose artifact", input.Placeholder);
        Assert.Equal(InputType.File, input.InputType);
        Assert.Equal(2048, input.MaxFileSize);
    }
#pragma warning restore ASPIREINTERACTION001

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(TestHostEnvironment);
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

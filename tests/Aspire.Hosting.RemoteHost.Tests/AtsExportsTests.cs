// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.Ats;
using Aspire.Hosting.Tests;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[Trait("Partition", "4")]
public class AtsExportsTests
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
#pragma warning restore ASPIREINTERACTION001

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(TestHostEnvironment);
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

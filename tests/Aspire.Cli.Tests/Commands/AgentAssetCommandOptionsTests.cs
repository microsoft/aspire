// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Tests.Commands;

public class AgentAssetCommandOptionsTests
{
    [Fact]
    public void Specs_AreOrderedAndMatchCatalogBackingShape()
    {
        Assert.Collection(
            AgentAssetCommandOptions.All,
            spec =>
            {
                var fileSpec = Assert.IsType<FileAssetCommandSpec>(spec);
                Assert.Equal(AgentAssetKind.Skill, fileSpec.AssetKind);
                Assert.All(
                    AgentAssetCatalog.All.Where(asset => asset.AssetKind == fileSpec.AssetKind),
                    static asset => Assert.IsType<AgentFileAssetDefinition>(asset));
            },
            spec =>
            {
                var actionSpec = Assert.IsType<ActionAssetCommandSpec>(spec);
                Assert.Equal(AgentAssetKind.Mcp, actionSpec.AssetKind);
                Assert.All(
                    AgentAssetCatalog.All.Where(asset => asset.AssetKind == actionSpec.AssetKind),
                    static asset => Assert.IsType<AgentActionAssetDefinition>(asset));
            });
    }

    [Fact]
    public void AddTo_IsIdempotent()
    {
        var command = new Command("test");

        AgentAssetCommandOptions.AddTo(command);
        AgentAssetCommandOptions.AddTo(command);

        var expectedOptions = AgentAssetCommandOptions.All.SelectMany(static spec => spec.Options).ToList();
        Assert.All(
            expectedOptions,
            option => Assert.Single(command.Options, candidate => ReferenceEquals(candidate, option)));
    }

    [Fact]
    public void Bind_DoesNotLeakValuesAcrossParseResults()
    {
        var command = new Command("test");
        AgentAssetCommandOptions.AddTo(command);
        var first = AgentAssetCommandOptions.Bind(command.Parse("--skills none --mcps aspire"));
        var second = AgentAssetCommandOptions.Bind(command.Parse(""));

        Assert.Equal(
            (true, ConsoleInteractionService.NoneChoice, (string?)null),
            PromptBinding.Resolve(first.GetFile(AgentAssetKind.Skill).Assets));
        Assert.Equal(
            (true, "aspire", (string?)null),
            PromptBinding.Resolve(first.GetAction(AgentAssetKind.Mcp).Assets));
        Assert.Equal(
            (false, (string?)null, (string?)null),
            PromptBinding.Resolve(second.GetFile(AgentAssetKind.Skill).Assets));
        Assert.Equal(
            (false, (string?)null, (string?)null),
            PromptBinding.Resolve(second.GetAction(AgentAssetKind.Mcp).Assets));
    }

    [Fact]
    public void ValidateSpecs_RejectsDuplicateKinds()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAssetCommandOptions.ValidateSpecs(
                [AgentAssetCommandOptions.Skills, AgentAssetCommandOptions.Skills]));

        Assert.Contains(nameof(AgentAssetKind.Skill), exception.Message);
    }

    [Fact]
    public void ValidateSpecs_RejectsDuplicateOptionAliases()
    {
        var duplicateAliasSpec = AgentAssetCommandOptions.Mcp with
        {
            AssetOption = AgentAssetCommandOptions.Skills.AssetOption,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAssetCommandOptions.ValidateSpecs(
                [AgentAssetCommandOptions.Skills, duplicateAliasSpec]));

        Assert.Contains(AgentAssetCommandOptions.Skills.AssetOption.Name, exception.Message);
    }

    [Fact]
    public void ValidateSpecs_RejectsMissingKinds()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentAssetCommandOptions.ValidateSpecs([AgentAssetCommandOptions.Skills]));

        Assert.Contains(nameof(AgentAssetKind.Mcp), exception.Message);
    }
}

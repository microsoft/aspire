// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

public class FoundryToolboxReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_CreatesFirstVersion()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            VersionToCreate = "1"
        };

        var result = await new FoundryToolboxReconciler(administration)
            .ReconcileAsync(definition, CancellationToken.None);

        Assert.Equal(FoundryToolboxReconcileAction.Created, result.Action);
        Assert.Equal("1", result.Version);
        Assert.Same(definition, Assert.Single(administration.CreatedDefinitions));
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_ReusesMatchingAspireManagedVersion()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            Existing = CreateExistingState(definition, "3")
        };

        var result = await new FoundryToolboxReconciler(administration)
            .ReconcileAsync(definition, CancellationToken.None);

        Assert.Equal(FoundryToolboxReconcileAction.Reused, result.Action);
        Assert.Equal("3", result.Version);
        Assert.Empty(administration.CreatedDefinitions);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_CreatesAndPromotesChangedAspireManagedVersion()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "3",
                [
                    CreateVersionState("3", "outdated")
                ]),
            VersionToCreate = "4"
        };

        var result = await new FoundryToolboxReconciler(administration)
            .ReconcileAsync(definition, CancellationToken.None);

        Assert.Equal(FoundryToolboxReconcileAction.CreatedAndPromoted, result.Action);
        Assert.Equal("4", result.Version);
        Assert.Same(definition, Assert.Single(administration.CreatedDefinitions));
        Assert.Equal(("field-tools", "4"), Assert.Single(administration.Promotions));
    }

    [Fact]
    public async Task ReconcileAsync_RejectsExistingToolboxNotManagedByAspire()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "1",
                [
                    new FoundryToolboxVersionState("1", new Dictionary<string, string>())
                ])
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("not managed by Aspire", exception.Message, StringComparison.Ordinal);
        Assert.Empty(administration.CreatedDefinitions);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_PromotesMatchingExistingVersion()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "3",
                [
                    CreateVersionState("3", "outdated"),
                    CreateVersionState("2", definition.ConfigurationHash)
                ])
        };

        var result = await new FoundryToolboxReconciler(administration)
            .ReconcileAsync(definition, CancellationToken.None);

        Assert.Equal(FoundryToolboxReconcileAction.Promoted, result.Action);
        Assert.Equal("2", result.Version);
        Assert.Empty(administration.CreatedDefinitions);
        Assert.Equal(("field-tools", "2"), Assert.Single(administration.Promotions));
    }

    [Fact]
    public async Task Create_ProducesStableConfigurationHash()
    {
        var firstTool = await new FoundryToolboxWebSearchToolDefinition("first").ResolveAsync(CancellationToken.None);
        var secondTool = await new FoundryToolboxWebSearchToolDefinition("second").ResolveAsync(CancellationToken.None);

        var first = FoundryToolboxDeploymentDefinition.Create(
            "field-tools",
            "Description",
            [firstTool, secondTool],
            new Dictionary<string, string>
            {
                ["b"] = "2",
                ["a"] = "1"
            });
        var second = FoundryToolboxDeploymentDefinition.Create(
            "field-tools",
            "Description",
            [secondTool, firstTool],
            new Dictionary<string, string>
            {
                ["a"] = "1",
                ["b"] = "2"
            });

        Assert.Equal(first.ConfigurationHash, second.ConfigurationHash);
    }

    [Fact]
    public async Task Create_RejectsDuplicateToolNames()
    {
        var first = await new FoundryToolboxWebSearchToolDefinition("search").ResolveAsync(CancellationToken.None);
        var second = await new FoundryToolboxWebSearchToolDefinition("search").ResolveAsync(CancellationToken.None);

        var exception = Assert.Throws<InvalidOperationException>(
            () => FoundryToolboxDeploymentDefinition.Create(
                "field-tools",
                "Description",
                [first, second],
                new Dictionary<string, string>()));

        Assert.Contains("duplicate tool names", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_AcceptsThirteenUserMetadataEntries()
    {
        var tool = await new FoundryToolboxWebSearchToolDefinition("search").ResolveAsync(CancellationToken.None);
        var metadata = Enumerable.Range(1, 13).ToDictionary(index => $"key-{index}", index => $"{index}");

        var definition = FoundryToolboxDeploymentDefinition.Create(
            "field-tools",
            "Description",
            [tool],
            metadata);

        Assert.Equal(16, definition.CreateDeploymentMetadata().Count);
    }

    [Fact]
    public async Task Create_RejectsFourteenUserMetadataEntries()
    {
        var tool = await new FoundryToolboxWebSearchToolDefinition("search").ResolveAsync(CancellationToken.None);
        var metadata = Enumerable.Range(1, 14).ToDictionary(index => $"key-{index}", index => $"{index}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => FoundryToolboxDeploymentDefinition.Create(
                "field-tools",
                "Description",
                [tool],
                metadata));

        Assert.Contains("at most 13 user metadata entries", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<FoundryToolboxDeploymentDefinition> CreateDefinitionAsync()
    {
        var tool = await new FoundryToolboxWebSearchToolDefinition("web-search")
            .ResolveAsync(CancellationToken.None);

        return FoundryToolboxDeploymentDefinition.Create(
            "field-tools",
            "Description",
            [tool],
            new Dictionary<string, string>());
    }

    private static FoundryToolboxState CreateExistingState(
        FoundryToolboxDeploymentDefinition definition,
        string version) =>
        new(
            version,
            [
                CreateVersionState(version, definition.ConfigurationHash)
            ]);

    private static FoundryToolboxVersionState CreateVersionState(string version, string hash) =>
        new(
            version,
            new Dictionary<string, string>
            {
                [FoundryToolboxDeploymentDefinition.ManagedByMetadataKey] =
                    FoundryToolboxDeploymentDefinition.ManagedByMetadataValue,
                [FoundryToolboxDeploymentDefinition.ConfigurationHashMetadataKey] = hash
            });

    private sealed class RecordingToolboxAdministration : IFoundryToolboxAdministration
    {
        public FoundryToolboxState? Existing { get; init; }

        public string VersionToCreate { get; init; } = "1";

        public List<FoundryToolboxDeploymentDefinition> CreatedDefinitions { get; } = [];

        public List<(string Name, string Version)> Promotions { get; } = [];

        public Task<FoundryToolboxState?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Existing);

        public Task<string> CreateVersionAsync(
            FoundryToolboxDeploymentDefinition definition,
            CancellationToken cancellationToken)
        {
            CreatedDefinitions.Add(definition);
            return Task.FromResult(VersionToCreate);
        }

        public Task PromoteVersionAsync(
            string name,
            string version,
            CancellationToken cancellationToken)
        {
            Promotions.Add((name, version));
            return Task.CompletedTask;
        }
    }
}

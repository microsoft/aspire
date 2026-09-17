// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

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

        Assert.Equal(FoundryToolboxReconcileAction.CreatedAndPromoted, result.Action);
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
    public async Task ReconcileAsync_RejectsMissingConsumerVersionAfterReconciliation()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            Existing = CreateExistingState(definition, "3")
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, "2", CancellationToken.None));

        Assert.Contains("does not contain version '2'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(administration.CreatedDefinitions);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_RejectsDefaultChangedBeforeReportingReuse()
    {
        var definition = await CreateDefinitionAsync();
        var initial = CreateExistingState(definition, "3");
        var concurrentlyChanged = new FoundryToolboxState(
            "4",
            [
                initial.Default,
                CreateVersionState("4", "other-deployment")
            ]);
        var administration = new RecordingToolboxAdministration();
        administration.GetResults.Enqueue(initial);
        administration.GetResults.Enqueue(concurrentlyChanged);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "version '4' with a different configuration is now the default",
            exception.Message,
            StringComparison.Ordinal);
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
    public async Task ReconcileAsync_RejectsForeignToolboxCreatedDuringInitialCreate()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            VersionToCreate = "2",
            StateAfterCreate = created => new FoundryToolboxState(
                "1",
                [
                    new FoundryToolboxVersionState("1", new Dictionary<string, string>()),
                    new FoundryToolboxVersionState(
                        created,
                        new Dictionary<string, string>(definition.CreateDeploymentMetadata()))
                ])
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not managed by Aspire", exception.Message, StringComparison.Ordinal);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_RejectsForeignDefaultBeforePromotingReusableVersion()
    {
        var definition = await CreateDefinitionAsync();
        var initial = new FoundryToolboxState(
            "3",
            [
                CreateVersionState("3", "outdated"),
                CreateVersionState("2", definition.ConfigurationHash)
            ]);
        var concurrentlyChanged = new FoundryToolboxState(
            "4",
            [
                new FoundryToolboxVersionState("4", new Dictionary<string, string>()),
                CreateVersionState("2", definition.ConfigurationHash)
            ]);
        var administration = new RecordingToolboxAdministration
        {
            Existing = initial
        };
        administration.GetResults.Enqueue(initial);
        administration.GetResults.Enqueue(concurrentlyChanged);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_RejectsManagedDefaultChangedBeforePromotion()
    {
        var definition = await CreateDefinitionAsync();
        var initial = new FoundryToolboxState(
            "3",
            [
                CreateVersionState("3", "outdated"),
                CreateVersionState("2", definition.ConfigurationHash)
            ]);
        var concurrentlyChanged = new FoundryToolboxState(
            "5",
            [
                .. initial.Versions,
                CreateVersionState("5", "other-deployment")
            ]);
        var administration = new RecordingToolboxAdministration();
        administration.GetResults.Enqueue(initial);
        administration.GetResults.Enqueue(concurrentlyChanged);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "expected default version '3', but version '5' is now the default",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_RejectsManagedToolboxCreatedConcurrently()
    {
        var definition = await CreateDefinitionAsync();
        var administration = new RecordingToolboxAdministration
        {
            VersionToCreate = "2",
            StateAfterCreate = created => new FoundryToolboxState(
                "1",
                [
                    CreateVersionState("1", "other-deployment"),
                    CreateVersionState(created, definition.ConfigurationHash)
                ])
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "expected no default version, but version '1' is now the default",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ReconcileAsync_VerifiesPromotedVersionRemainsDefault()
    {
        var definition = await CreateDefinitionAsync();
        var initial = new FoundryToolboxState(
            "3",
            [
                CreateVersionState("3", "outdated"),
                CreateVersionState("2", definition.ConfigurationHash)
            ]);
        var administration = new RecordingToolboxAdministration
        {
            Existing = initial,
            StateAfterPromotion = (_, _) => initial
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxReconciler(administration)
                .ReconcileAsync(definition, CancellationToken.None));

        Assert.Contains("changed concurrently", exception.Message, StringComparison.Ordinal);
        Assert.Contains("version '3' is now the default", exception.Message, StringComparison.Ordinal);
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
    public async Task Create_ToolDescriptionAndApprovalParticipateInConfigurationHash()
    {
        var baselineWeb = await new FoundryToolboxWebSearchToolDefinition("web")
            .ResolveAsync(CancellationToken.None);
        var describedWeb = await new FoundryToolboxWebSearchToolDefinition("web", "Search the web.")
            .ResolveAsync(CancellationToken.None);
        var baselineMcp = await new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"https://inventory.example.com/mcp"))
            .ResolveAsync(CancellationToken.None);
        var approvalMcp = await new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"https://inventory.example.com/mcp"),
            new FoundryToolboxMcpToolOptions
            {
                ApprovalPolicy = new()
                {
                    Global = FoundryToolboxMcpGlobalApprovalMode.Always
                }
            })
            .ResolveAsync(CancellationToken.None);

        var baselineWebHash = CreateDefinition([baselineWeb]).ConfigurationHash;
        var describedWebHash = CreateDefinition([describedWeb]).ConfigurationHash;
        var baselineMcpHash = CreateDefinition([baselineMcp]).ConfigurationHash;
        var approvalMcpHash = CreateDefinition([approvalMcp]).ConfigurationHash;

        Assert.NotEqual(baselineWebHash, describedWebHash);
        Assert.NotEqual(baselineMcpHash, approvalMcpHash);
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
    public async Task Create_RejectsDuplicateMcpServerLabels()
    {
        var first = await new FoundryToolboxMcpToolDefinition(
            "first",
            ReferenceExpression.Create($"https://first.example.com/mcp"),
            new FoundryToolboxMcpToolOptions { ServerLabel = "shared" })
            .ResolveAsync(CancellationToken.None);
        var second = await new FoundryToolboxMcpToolDefinition(
            "second",
            ReferenceExpression.Create($"https://second.example.com/mcp"),
            new FoundryToolboxMcpToolOptions { ServerLabel = "shared" })
            .ResolveAsync(CancellationToken.None);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateDefinition([first, second]));

        Assert.Contains("duplicate MCP server labels: shared", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task ValidateExistingAsync_UsesDefaultVersionWithoutOwnershipRequirements()
    {
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "3",
                [
                    new FoundryToolboxVersionState("3", new Dictionary<string, string>())
                ])
        };

        var version = await new FoundryToolboxExistingResourceValidator(administration)
            .ValidateAsync("field-tools", version: null, CancellationToken.None);

        Assert.Equal("3", version);
        Assert.Empty(administration.CreatedDefinitions);
        Assert.Empty(administration.Promotions);
    }

    [Fact]
    public async Task ValidateExistingAsync_UsesPinnedVersion()
    {
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "3",
                [
                    new FoundryToolboxVersionState("2", new Dictionary<string, string>()),
                    new FoundryToolboxVersionState("3", new Dictionary<string, string>())
                ])
        };

        var version = await new FoundryToolboxExistingResourceValidator(administration)
            .ValidateAsync("field-tools", "2", CancellationToken.None);

        Assert.Equal("2", version);
    }

    [Fact]
    public async Task ValidateExistingAsync_RejectsMissingToolbox()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxExistingResourceValidator(new RecordingToolboxAdministration())
                .ValidateAsync("field-tools", version: null, CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateExistingAsync_RejectsMissingPinnedVersion()
    {
        var administration = new RecordingToolboxAdministration
        {
            Existing = new FoundryToolboxState(
                "3",
                [
                    new FoundryToolboxVersionState("3", new Dictionary<string, string>())
                ])
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FoundryToolboxExistingResourceValidator(administration)
                .ValidateAsync("field-tools", "2", CancellationToken.None));

        Assert.Contains("does not contain version '2'", exception.Message, StringComparison.Ordinal);
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

    private static FoundryToolboxDeploymentDefinition CreateDefinition(
        IReadOnlyList<ResolvedFoundryToolboxTool> tools) =>
        FoundryToolboxDeploymentDefinition.Create(
            "field-tools",
            "Description",
            tools,
            new Dictionary<string, string>());

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
        public FoundryToolboxState? Existing { get; set; }

        public string VersionToCreate { get; init; } = "1";

        public Func<string, FoundryToolboxState>? StateAfterCreate { get; init; }

        public Func<string, string, FoundryToolboxState>? StateAfterPromotion { get; init; }

        public Queue<FoundryToolboxState?> GetResults { get; } = [];

        public List<FoundryToolboxDeploymentDefinition> CreatedDefinitions { get; } = [];

        public List<(string Name, string Version)> Promotions { get; } = [];

        public Task<FoundryToolboxState?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(GetResults.TryDequeue(out var state) ? state : Existing);

        public Task<string> CreateVersionAsync(
            FoundryToolboxDeploymentDefinition definition,
            CancellationToken cancellationToken)
        {
            CreatedDefinitions.Add(definition);
            Existing = StateAfterCreate?.Invoke(VersionToCreate) ??
                new FoundryToolboxState(
                    Existing?.DefaultVersion ?? VersionToCreate,
                    [
                        .. (Existing?.Versions ?? []),
                        new FoundryToolboxVersionState(
                            VersionToCreate,
                            new Dictionary<string, string>(definition.CreateDeploymentMetadata()))
                    ]);
            return Task.FromResult(VersionToCreate);
        }

        public Task PromoteVersionAsync(
            string name,
            string version,
            CancellationToken cancellationToken)
        {
            Promotions.Add((name, version));
            Existing = StateAfterPromotion?.Invoke(name, version) ??
                (Existing is { } existing
                    ? new FoundryToolboxState(version, existing.Versions)
                    : Existing);
            return Task.CompletedTask;
        }
    }
}

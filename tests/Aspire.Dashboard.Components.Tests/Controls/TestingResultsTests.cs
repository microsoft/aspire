// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class TestingResultsTests : DashboardTestContext
{
    [Fact]
    public void LiveUpdatesGroupReplicasAndShowOneResourceOnlyRow()
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesPage(this, new ViewportInformation(true, false, false),
            new TestDashboardClient(isEnabled: true, initialResources:
            [
                TestResource("cargo", "cargo", KnownResourceState.Running),
                TestResource("structured-1", "structured", KnownResourceState.Running),
                TestResource("structured-2", "structured", KnownResourceState.Running),
                ModelTestHelpers.CreateResource("service", state: KnownResourceState.Running)
            ], resourceChannelProvider: () => channel));
        var cut = Render<TestingResults>();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["cargo", "structured"], cut.FindAll("section").Select(s => s.GetAttribute("aria-label")));
            Assert.Equal(2, cut.FindAll(".testing-fallback").Count);
        });
        var results = Value.ForList(Value.ForStruct(new Struct
        {
            Fields = { ["name"] = Value.ForString("test one"), ["outcome"] = Value.ForString("passed") }
        }), Value.ForStruct(new Struct
        {
            Fields = { ["name"] = Value.ForString("test two"), ["outcome"] = Value.ForString("skipped") }
        }));
        var snapshot = Value.ForStruct(new Struct
        {
            Fields =
            {
                ["revision"] = Value.ForNumber(3), ["resourceOutcome"] = Value.ForString("failed"),
                ["collectionStatus"] = Value.ForString("failed"), ["collectionError"] = Value.ForString("collector failed"),
                ["results"] = results
            }
        });
        channel.Writer.TryWrite([new(ResourceViewModelChangeType.Upsert, TestResource("structured-2", "structured", KnownResourceState.Finished, snapshot))]);
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".testing-fallback"));
            var section = cut.Find("section[aria-label='structured']");
            Assert.Equal("structured Failed", section.QuerySelector("h2")!.TextContent.Trim());
            Assert.Equal("collector failed", section.QuerySelector("[role='alert']")!.TextContent);
            Assert.Equal(["test one", "test two"], section.QuerySelectorAll("[role='row']").Skip(1).Select(r => r.QuerySelector("[role='gridcell']")!.TextContent.Trim()));
        });
        channel.Writer.TryWrite([new(ResourceViewModelChangeType.Delete, TestResource("cargo", "cargo", KnownResourceState.Finished))]);
        cut.WaitForAssertion(() => Assert.Equal("structured", Assert.Single(cut.FindAll("section")).GetAttribute("aria-label")));
    }

    [Fact]
    public void NonTestResourcesHaveAnExplicitEmptyState()
    {
        ResourceSetupHelpers.SetupResourcesPage(this, new ViewportInformation(true, false, false),
            new TestDashboardClient(isEnabled: true, initialResources: [ModelTestHelpers.CreateResource("service")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<TestingResults>();
        cut.WaitForAssertion(() => Assert.Equal("No testing resources in this selection.", cut.Find("[role='status']").TextContent));
        Assert.Empty(cut.FindAll("section"));
    }

    [Fact]
    public void LegacyResourceSelectionFiltersTheSharedTable()
    {
        ResourceSetupHelpers.SetupResourcesPage(this, new ViewportInformation(true, false, false),
            new TestDashboardClient(isEnabled: true, initialResources:
                [TestResource("tests-1", "tests", KnownResourceState.Running), TestResource("other", "other", KnownResourceState.Running)],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<TestingResults>(p => p.Add(c => c.ResourceName, "tests"));
        cut.WaitForAssertion(() => Assert.Equal("tests", Assert.Single(cut.FindAll("section")).GetAttribute("aria-label")));
    }

    [Fact]
    public void CompletedResultsReplayOnAReplacementSubscription()
    {
        var snapshot = Value.ForStruct(new Struct
        {
            Fields =
            {
                ["revision"] = Value.ForNumber(3), ["resourceOutcome"] = Value.ForString("passed"),
                ["collectionStatus"] = Value.ForString("completed"),
                ["results"] = Value.ForList(Value.ForStruct(new Struct
                {
                    Fields = { ["name"] = Value.ForString("replayed test"), ["outcome"] = Value.ForString("passed") }
                }))
            }
        });
        ResourceSetupHelpers.SetupResourcesPage(this, new ViewportInformation(true, false, false),
            new TestDashboardClient(isEnabled: true, initialResources: [TestResource("tests", "tests", KnownResourceState.Finished, snapshot)],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var first = Render<TestingResults>();
        first.WaitForAssertion(() => Assert.Equal(["replayed test"], first.Instance.Groups.Single().Results.Select(r => r.Name)));
        first.Dispose();
        var replay = Render<TestingResults>();
        replay.WaitForAssertion(() =>
        {
            Assert.Equal("passed", Assert.Single(replay.Instance.Groups).Outcome);
            Assert.Equal(["replayed test"], replay.Instance.Groups.Single().Results.Select(r => r.Name));
        });
    }

    [Fact]
    public void SubscriptionFailureIsVisibleInsteadOfAnEmptySuccessState()
    {
        var client = new TestDashboardClient(isEnabled: true)
        {
            BeforeResourceSubscriptionAsync = _ => throw new IOException("fixture stream failure")
        };
        ResourceSetupHelpers.SetupResourcesPage(this, new ViewportInformation(true, false, false), client);
        var cut = Render<TestingResults>();
        cut.WaitForAssertion(() => Assert.Equal("Unable to load testing resources. Results may be incomplete.", cut.Find("[role='alert']").TextContent));
    }

    private static ResourceViewModel TestResource(string name, string displayName, KnownResourceState state, Value? snapshot = null)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Resource.Testing] = new(KnownProperties.Resource.Testing, Value.ForString("true"), false, null, 0, null, false)
        };
        if (snapshot is not null)
        {
            properties[KnownProperties.Resource.TestingResults] = new(KnownProperties.Resource.TestingResults, snapshot, false, null, 0, null, false);
        }
        return ModelTestHelpers.CreateResource(name, displayName: displayName, state: state, properties: properties);
    }
}

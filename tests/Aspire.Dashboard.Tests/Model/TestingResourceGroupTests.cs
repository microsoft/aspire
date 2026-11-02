// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class TestingResourceGroupTests
{
    [Theory]
    [InlineData("passed", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("skipped", "passed")]
    [InlineData("inconclusive", "failed")]
    [InlineData("error", "failed")]
    [InlineData("timedOut", "failed")]
    [InlineData("cancelled", "failed")]
    public void EveryPublishedOutcomeIsPreservedAndAggregated(string outcome, string expected)
    {
        var snapshot = Snapshot(3, "passed", "completed", Result("passing test", "passed"), Result("test", outcome));
        var group = TestingResourceGroup.Create([Resource("tests-1", snapshot)]);
        Assert.False(group.HasInvalidData);
        Assert.Equal(expected, group.Outcome);
        Assert.Equal(["passed", outcome], group.Results.Select(r => r.Outcome));
        Assert.Equal(expected == "failed", TestingResultRow.IsFailure(outcome));
    }

    [Theory]
    [InlineData(KnownResourceState.Starting, null, "pending")]
    [InlineData(KnownResourceState.Running, null, "running")]
    [InlineData(KnownResourceState.Finished, null, "unknown")]
    [InlineData(KnownResourceState.Finished, 0, "passed")]
    [InlineData(KnownResourceState.Exited, 1, "failed")]
    [InlineData(KnownResourceState.FailedToStart, null, "failed")]
    public void ResourceOnlyOutcomeDoesNotInventIndividualResults(KnownResourceState state, int? exitCode, string expected)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>();
        if (exitCode is { } code)
        {
            properties[KnownProperties.Resource.ExitCode] = new(KnownProperties.Resource.ExitCode, Value.ForNumber(code), false, null, 0, null, false);
        }
        var resource = ModelTestHelpers.CreateResource("tests", state: state, properties: properties);
        var group = TestingResourceGroup.Create([resource]);
        Assert.Equal(expected, group.Outcome);
        Assert.Empty(group.Results);
    }

    [Theory]
    [InlineData("passed", "completed", "passed")]
    [InlineData("failed", "completed", "failed")]
    [InlineData("passed", "failed", "failed")]
    [InlineData("passed", "running", "running")]
    [InlineData("passed", "cancelled", "cancelled")]
    [InlineData("pending", "completed", "unknown")]
    public void PassingTestsDoNotConcealExecutionOrCollectionOutcome(string resourceOutcome, string collectionStatus, string expected)
    {
        var snapshot = Snapshot(1, resourceOutcome, collectionStatus, Result("pass", "passed"), Result("skip", "skipped"));
        var group = TestingResourceGroup.Create([Resource("tests-1", snapshot)]);
        Assert.Equal(expected, group.Outcome);
        Assert.Equal("tests", group.ResourceName);
        Assert.Collection(group.Results,
            row => Assert.Equal(new TestingResultRow("pass", "passed", 12, "fixture message"), row),
            row => Assert.Equal(new TestingResultRow("skip", "skipped", 12, "fixture message"), row));
    }

    [Fact]
    public void LatestReplicaSnapshotKeepsOneLogicalResultSet()
    {
        var oldSnapshot = Snapshot(1, "pending", "running", Result("pass", "passed"));
        var newSnapshot = Snapshot(3, "passed", "completed", Result("pass", "passed"), Result("failure", "failed"));
        var group = TestingResourceGroup.Create([Resource("tests-1", oldSnapshot), Resource("tests-2", newSnapshot)]);
        Assert.Equal("failed", group.Outcome);
        Assert.Equal("completed", group.CollectionStatus);
        Assert.Equal(["pass", "failure"], group.Results.Select(r => r.Name));
    }

    [Theory]
    [InlineData("not a result")]
    [InlineData("timedout")]
    [InlineData("unsupported")]
    public void MalformedResultsAreVisibleAsUnknown(string outcome)
    {
        var snapshot = Snapshot(1, "passed", "completed",
            outcome == "not a result" ? Value.ForString(outcome) : Result("fixture", outcome));
        var group = TestingResourceGroup.Create([Resource("tests-1", snapshot)]);
        Assert.True(group.HasInvalidData);
        Assert.Equal("unknown", group.Outcome);
        Assert.Equal("unknown", Assert.Single(group.Results).Outcome);
    }

    [Fact]
    public void OneUnknownReplicaPreventsResourceOnlySuccess()
    {
        var passed = ModelTestHelpers.CreateResource("tests-1", displayName: "tests", state: KnownResourceState.Finished, properties: new()
        {
            [KnownProperties.Resource.ExitCode] = new(KnownProperties.Resource.ExitCode, Value.ForNumber(0), false, null, 0, null, false)
        });
        var unknown = ModelTestHelpers.CreateResource("tests-2", displayName: "tests", state: KnownResourceState.Finished);
        Assert.Equal("unknown", TestingResourceGroup.Create([passed, unknown]).Outcome);
    }

    [Fact]
    public void MissingExpectedReplicasCannotPassDuringAnOrdinaryRun()
    {
        var resource = ModelTestHelpers.CreateResource("tests-1", displayName: "tests", state: KnownResourceState.Finished, properties: new()
        {
            [KnownProperties.Resource.ExitCode] = new(KnownProperties.Resource.ExitCode, Value.ForNumber(0), false, null, 0, null, false),
            [KnownProperties.Resource.TestingExpectedReplicas] = new(KnownProperties.Resource.TestingExpectedReplicas, Value.ForNumber(2), false, null, 0, null, false)
        });
        Assert.Equal("unknown", TestingResourceGroup.Create([resource]).Outcome);
    }
    private static ResourceViewModel Resource(string name, Value snapshot) =>
        ModelTestHelpers.CreateResource(name, displayName: "tests", state: KnownResourceState.Finished, properties: new()
        {
            [KnownProperties.Resource.TestingResults] = new(KnownProperties.Resource.TestingResults, snapshot, false, null, 0, null, false)
        });

    private static Value Snapshot(int revision, string resourceOutcome, string collectionStatus, params Value[] results)
    {
        var value = new Struct();
        value.Fields["revision"] = Value.ForNumber(revision);
        value.Fields["resourceOutcome"] = Value.ForString(resourceOutcome);
        value.Fields["collectionStatus"] = Value.ForString(collectionStatus);
        value.Fields["results"] = Value.ForList(results);
        return new Value { StructValue = value };
    }

    private static Value Result(string name, string outcome)
    {
        var value = new Struct();
        value.Fields["name"] = Value.ForString(name);
        value.Fields["outcome"] = Value.ForString(outcome);
        value.Fields["durationMilliseconds"] = Value.ForNumber(12);
        value.Fields["message"] = Value.ForString("fixture message");
        return new Value { StructValue = value };
    }
}

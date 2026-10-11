// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Sessions;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeDiagnosticsTests
{
    [Fact]
    public void ResourceOperationsCorrelateToTheCallerAndGeneration()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example/Resource");
        model.Seal();

        var activities = capture.Activities.ToArray();
        Assert.Equal(["model.resource.add", "model.seal"], activities.Select(activity => activity.OperationName));
        Assert.All(activities, activity =>
        {
            Assert.Equal(capture.Root.TraceId, activity.TraceId);
            Assert.Equal(capture.Root.SpanId, activity.ParentSpanId);
            Assert.Equal(ActivityStatusCode.Ok, activity.Status);
            Assert.Equal(model.GenerationId, activity.GetTagItem("aspire.generation.id"));
        });
        Assert.Equal(resource.ResourceId, activities[0].GetTagItem("aspire.resource.id"));
        var events = capture.Events.ToArray();
        Assert.Equal(["started", "ok", "started", "ok"], events.Select(value => value.Outcome));
        Assert.Equal(events[0].OperationId, events[1].OperationId);
        Assert.NotEqual(Guid.Empty, events[0].OperationId);
        Assert.NotEqual(events[0].OperationId, events[2].OperationId);
        Assert.Equal(resource.ResourceId, events[1].ResourceId);
        Assert.All(events, value => Assert.Equal(capture.Root.TraceId.ToString(), value.TraceId));
    }

    [Fact]
    public void ErrorsPreserveTheExceptionButDoNotExportItsMessage()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        var exception = new InvalidOperationException("sensitive-value-that-must-not-be-exported");

        var observed = Assert.Throws<InvalidOperationException>(() =>
            NativeDiagnostics.Execute("test.failure", null, Guid.NewGuid(), _ => throw exception));

        Assert.Same(exception, observed);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem("error.type"));
        Assert.Equal(["aspire.generation.id", "error.type"], activity.TagObjects.Select(tag => tag.Key));
        var failed = Assert.Single(capture.Events, value => value.Outcome == "error");
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.ErrorType);
        Assert.Null(failed.ResourceId);
    }

    [Fact]
    public void ValidationAndOwnershipFailuresAreVisible()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example/Resource");
        model.Seal();

        Assert.Throws<ArgumentException>(() => model.AddResource("invalid/name", "secret-input"));
        Assert.Throws<InvalidOperationException>(() => model.AddDependency(resource, resource));

        var failed = capture.Events.Where(value => value.Outcome == "error").ToArray();
        Assert.Equal(["model.resource.add", "model.dependency.add"], failed.Select(value => value.Operation));
        Assert.Equal([typeof(ArgumentException).FullName, typeof(InvalidOperationException).FullName],
            failed.Select(value => value.ErrorType));
        Assert.All(failed, value => Assert.Equal(model.GenerationId, value.GenerationId));
    }

    [Fact]
    public void StructuredDiagnosticsDoNotDependOnTraceSampling()
    {
        using var capture = new DiagnosticCapture(sampleActivities: false);
        using var model = new ApplicationModel();
        model.AddResource("cache", "example/Resource");

        Assert.Empty(capture.Activities);
        Assert.Equal(["started", "ok"], capture.Events.Select(value => value.Outcome));
        Assert.All(capture.Events, value => Assert.Equal(capture.Root.TraceId.ToString(), value.TraceId));
        Assert.Equal(2, capture.Measurements.Count);
    }

    [Fact]
    public void MetricsHaveOnlyBoundedDimensions()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        using var model = new ApplicationModel();
        model.AddResource("cache", "example/Resource");
        Assert.Throws<InvalidOperationException>(() => model.AddResource("cache", "example/Resource"));

        var measurements = capture.Measurements.ToArray();
        Assert.Equal(4, measurements.Length);
        Assert.All(measurements, value =>
        {
            Assert.Equal(["operation", "outcome"], value.Tags.Select(tag => tag.Key));
            Assert.Equal("model.resource.add", value.Tags[0].Value);
            Assert.True(value.Value >= 0);
        });
        Assert.Equal(["ok", "error"], measurements.Where(value => value.Name == "aspire.native.operations")
            .Select(value => value.Tags[1].Value));
        Assert.All(measurements.Where(value => value.Name == "aspire.native.operations"),
            value => Assert.Equal(1, value.Value));
    }

    [Fact]
    public void SessionRetirementLinksToItsModelRetirement()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        using var session = new ApplicationSession();
        var model = session.StartGeneration();
        session.RetireGeneration(model.GenerationId);

        var sessionRetirement = Assert.Single(capture.Activities, activity => activity.OperationName == "session.generation.retire");
        var modelRetirement = Assert.Single(capture.Activities, activity => activity.OperationName == "model.retire");
        Assert.Equal(sessionRetirement.SpanId, modelRetirement.ParentSpanId);
        Assert.Equal(session.SessionId, sessionRetirement.GetTagItem("aspire.session.id"));
        Assert.Equal(model.GenerationId, modelRetirement.GetTagItem("aspire.generation.id"));
        var start = Assert.Single(capture.Events, value => value.Operation == "session.generation.start" && value.Outcome == "ok");
        Assert.Equal(session.SessionId, start.SessionId);
        Assert.Equal(model.GenerationId, start.GenerationId);
    }
}

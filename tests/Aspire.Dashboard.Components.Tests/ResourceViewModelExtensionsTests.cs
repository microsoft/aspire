// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Components.Tests;

public class ResourceViewModelExtensionsTests
{
    [Fact]
    public void ExitedResourceIsStopped()
    {
        var resource = new ResourceViewModel
        {
            Name = "resource",
            ResourceType = KnownResourceTypes.Project,
            DisplayName = "resource",
            Uid = "resource",
            ReplicaIndex = 0,
            State = nameof(KnownResourceState.Exited),
            KnownState = KnownResourceState.Exited,
            StateStyle = null,
            CreationTimeStamp = null,
            StartTimeStamp = null,
            StopTimeStamp = null,
            Environment = [],
            Urls = [],
            Volumes = [],
            Relationships = [],
            Properties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty,
            Commands = [],
            HealthReports = []
        };

        Assert.True(resource.IsStopped());
    }
}

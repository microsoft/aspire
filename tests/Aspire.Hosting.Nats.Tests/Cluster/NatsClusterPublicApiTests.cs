// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Nats.Tests;

public class NatsClusterPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithMemberThrowsWhenBuilderIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var member = appBuilder.AddNats("mongo1");

        IResourceBuilder<NatsClusterResource> builder = null!;
        var action = () => builder.WithMember(member);

        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void WithMemberThrowsWhenMemberIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cluster = appBuilder.AddNatsCluster("mongo1");

        var action = () => cluster.WithMember(null!);

        Assert.Throws<ArgumentNullException>(action);
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.MongoDB.Tests;

public class MongoDBReplicaSetPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithMemberThrowsWhenBuilderIsNull()
    {
        IResourceBuilder<MongoDBReplicaSetResource> builder = null!;
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var member = appBuilder.AddMongoDB("mongo1");

        var action = () => builder.WithMember(member);

        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void WithMemberThrowsWhenMemberIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var rs = builder.AddMongoDBReplicaSet("rs0");

        var action = () => rs.WithMember(null!);

        Assert.Throws<ArgumentNullException>(action);
    }
}

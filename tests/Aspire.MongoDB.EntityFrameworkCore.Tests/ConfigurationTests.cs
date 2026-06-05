// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.MongoDB.EntityFrameworkCore.Tests;

public class ConfigurationTests
{
    [Fact]
    public void ConnectionStringIsNullByDefault()
        => Assert.Null(new MongoDBEntityFrameworkCoreSettings().ConnectionString);

    [Fact]
    public void DatabaseNameStringIsNullByDefault()
            => Assert.Null(new MongoDBEntityFrameworkCoreSettings().DatabaseName);

    [Fact]
    public void HealthCheckIsEnabledByDefault()
        => Assert.False(new MongoDBEntityFrameworkCoreSettings().DisableHealthChecks);

    [Fact]
    public void TracingIsEnabledByDefault()
        => Assert.False(new MongoDBEntityFrameworkCoreSettings().DisableTracing);
}

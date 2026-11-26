// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Microting.EntityFrameworkCore.MySql.Tests;

public class ConfigurationTests
{
    [Fact]
    public void ConnectionStringIsNullByDefault()
        => Assert.Null(new MicrotingEntityFrameworkCoreMySqlSettings().ConnectionString);

    [Fact]
    public void HealthCheckIsEnabledByDefault()
        => Assert.False(new MicrotingEntityFrameworkCoreMySqlSettings().DisableHealthChecks);

    [Fact]
    public void TracingIsEnabledByDefault()
        => Assert.False(new MicrotingEntityFrameworkCoreMySqlSettings().DisableTracing);

    [Fact]
    public void MetricsAreEnabledByDefault()
        => Assert.False(new MicrotingEntityFrameworkCoreMySqlSettings().DisableMetrics);

    [Fact]
    public void RetriesAreEnabledByDefault()
        => Assert.False(new MicrotingEntityFrameworkCoreMySqlSettings().DisableRetry);
}

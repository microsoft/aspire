// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.StackExchange.Redis.Tests;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.StackExchange.Redis.OutputCaching.Tests;

public class OutputCacheConformanceTests : ConformanceTests
{
    // Schema only references Aspire.StackExchange.Redis' schema so nothing
    // specific to check here
    protected override (string json, string error)[] InvalidJsonToErrorMessage => Array.Empty<(string json, string error)>();

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<StackExchangeRedisSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddRedisOutputCache("redis", configure);
        }
        else
        {
            builder.AddKeyedRedisOutputCache(key, configure);
        }
    }

    public OutputCacheConformanceTests(RedisContainerFixture containerFixture, ITestOutputHelper? output = null) : base(containerFixture, output)
    {
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WorksWithOpenTelemetryTracing()
    {
        RemoteExecutor.Invoke(async (connectionString) =>
        {
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("ConnectionStrings:redis", connectionString)
            ]);

            builder.AddRedisOutputCache("redis");

            using var notifier = new ActivityNotifier();
            builder.Services.AddOpenTelemetry().WithTracing(builder => builder.AddProcessor(notifier));
            // set the FlushInterval to to zero so the Activity gets created immediately
            builder.Services.Configure<StackExchangeRedisInstrumentationOptions>(options => options.FlushInterval = TimeSpan.Zero);

            using var host = builder.Build();
            // We start the host to make it build TracerProvider.
            // If we don't, nothing gets reported!
            host.Start();

            var cacheStore = host.Services.GetRequiredService<IOutputCacheStore>();
            await cacheStore.GetAsync("myFakeKey", CancellationToken.None);

            // read the first 3 activities
            var activityList = await notifier.TakeAsync(3, TimeSpan.FromSeconds(10));
            Assert.Equal(3, activityList.Count);
            Assert.Collection(activityList,
                // https://github.com/dotnet/aspnetcore/pull/54239 added 2 CLIENT activities on the first call
                activity =>
                {
                    Assert.Equal("CLIENT", activity.OperationName);
                    Assert.Contains(activity.Tags, kvp => kvp.Key == "db.system" && kvp.Value == "redis");
                },
                activity =>
                {
                    Assert.Equal("CLIENT", activity.OperationName);
                    Assert.Contains(activity.Tags, kvp => kvp.Key == "db.system" && kvp.Value == "redis");
                },
                activity =>
                {
                    Assert.Equal("GET", activity.OperationName);
                    Assert.Contains(activity.Tags, kvp => kvp.Key == "db.system" && kvp.Value == "redis");
                });
        }, ConnectionString).Dispose();
    }
}

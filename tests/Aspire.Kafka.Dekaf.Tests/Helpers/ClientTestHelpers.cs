// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Dekaf.Consumer;
using Dekaf.Producer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

internal static class ClientTestHelpers
{
    internal static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:messaging"] = "localhost:19092",
            ["Aspire:Kafka:Dekaf:Consumer:Config:GroupId"] = "test-group"
        });
        return builder;
    }

    internal static T GetOptions<T>(object client)
    {
        // Dekaf intentionally does not expose its effective options publicly. Inspect the immutable
        // options retained by the client to verify binding and fluent overrides without a broker.
        var field = client.GetType().GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(client));
    }

    internal static IKafkaProducer<string, string> GetProducer(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<IKafkaProducer<string, string>>("messaging") : services.GetRequiredService<IKafkaProducer<string, string>>();

    internal static IKafkaConsumer<string, string> GetConsumer(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<IKafkaConsumer<string, string>>("messaging") : services.GetRequiredService<IKafkaConsumer<string, string>>();
}

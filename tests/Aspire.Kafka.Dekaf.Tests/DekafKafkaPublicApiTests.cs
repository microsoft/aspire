// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class DekafKafkaPublicApiTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AddDekafKafkaConsumerShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "Kafka:Consumer";
        Action<KafkaConsumerSettings>? configureSettings = null;
        Action<ConsumerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ConsumerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddDekafKafkaConsumer<string, string>(connectionName),
            1 => () => builder.AddDekafKafkaConsumer<string, string>(connectionName, configureSettings),
            2 => () => builder.AddDekafKafkaConsumer(connectionName, configureBuilder),
            3 => () => builder.AddDekafKafkaConsumer(connectionName, configureBuilderWithServiceProvider),
            4 => () => builder.AddDekafKafkaConsumer(connectionName, configureSettings, configureBuilder),
            5 => () => builder.AddDekafKafkaConsumer(connectionName, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void AddDekafKafkaConsumerShouldThrowWhenConnectionNameIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;
        Action<KafkaConsumerSettings>? configureSettings = null;
        Action<ConsumerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ConsumerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddDekafKafkaConsumer<string, string>(connectionName),
            1 => () => builder.AddDekafKafkaConsumer<string, string>(connectionName, configureSettings),
            2 => () => builder.AddDekafKafkaConsumer(connectionName, configureBuilder),
            3 => () => builder.AddDekafKafkaConsumer(connectionName, configureBuilderWithServiceProvider),
            4 => () => builder.AddDekafKafkaConsumer(connectionName, configureSettings, configureBuilder),
            5 => () => builder.AddDekafKafkaConsumer(connectionName, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AddKeyedDekafKafkaConsumerShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IHostApplicationBuilder builder = null!;
        const string name = "Kafka:Consumer";
        Action<KafkaConsumerSettings>? configureSettings = null;
        Action<ConsumerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ConsumerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddKeyedDekafKafkaConsumer<string, string>(name),
            1 => () => builder.AddKeyedDekafKafkaConsumer<string, string>(name, configureSettings),
            2 => () => builder.AddKeyedDekafKafkaConsumer(name, configureBuilder),
            3 => () => builder.AddKeyedDekafKafkaConsumer(name, configureBuilderWithServiceProvider),
            4 => () => builder.AddKeyedDekafKafkaConsumer(name, configureSettings, configureBuilder),
            5 => () => builder.AddKeyedDekafKafkaConsumer(name, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void AddKeyedDekafKafkaConsumerShouldThrowWhenConnectionNameIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;
        Action<KafkaConsumerSettings>? configureSettings = null;
        Action<ConsumerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ConsumerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddKeyedDekafKafkaConsumer<string, string>(name),
            1 => () => builder.AddKeyedDekafKafkaConsumer<string, string>(name, configureSettings),
            2 => () => builder.AddKeyedDekafKafkaConsumer(name, configureBuilder),
            3 => () => builder.AddKeyedDekafKafkaConsumer(name, configureBuilderWithServiceProvider),
            4 => () => builder.AddKeyedDekafKafkaConsumer(name, configureSettings, configureBuilder),
            5 => () => builder.AddKeyedDekafKafkaConsumer(name, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AddDekafKafkaProducerShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "Kafka:Consumer";
        Action<KafkaProducerSettings>? configureSettings = null;
        Action<ProducerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ProducerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddDekafKafkaProducer<string, string>(connectionName),
            1 => () => builder.AddDekafKafkaProducer<string, string>(connectionName, configureSettings),
            2 => () => builder.AddDekafKafkaProducer(connectionName, configureBuilder),
            3 => () => builder.AddDekafKafkaProducer(connectionName, configureBuilderWithServiceProvider),
            4 => () => builder.AddDekafKafkaProducer(connectionName, configureSettings, configureBuilder),
            5 => () => builder.AddDekafKafkaProducer(connectionName, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void AddDekafKafkaProducerShouldThrowWhenConnectionNameIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;
        Action<KafkaProducerSettings>? configureSettings = null;
        Action<ProducerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ProducerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddDekafKafkaProducer<string, string>(connectionName),
            1 => () => builder.AddDekafKafkaProducer<string, string>(connectionName, configureSettings),
            2 => () => builder.AddDekafKafkaProducer(connectionName, configureBuilder),
            3 => () => builder.AddDekafKafkaProducer(connectionName, configureBuilderWithServiceProvider),
            4 => () => builder.AddDekafKafkaProducer(connectionName, configureSettings, configureBuilder),
            5 => () => builder.AddDekafKafkaProducer(connectionName, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AddKeyedDekafKafkaProducerShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IHostApplicationBuilder builder = null!;
        const string name = "Kafka:Consumer";
        Action<KafkaProducerSettings>? configureSettings = null;
        Action<ProducerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ProducerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddKeyedDekafKafkaProducer<string, string>(name),
            1 => () => builder.AddKeyedDekafKafkaProducer<string, string>(name, configureSettings),
            2 => () => builder.AddKeyedDekafKafkaProducer(name, configureBuilder),
            3 => () => builder.AddKeyedDekafKafkaProducer(name, configureBuilderWithServiceProvider),
            4 => () => builder.AddKeyedDekafKafkaProducer(name, configureSettings, configureBuilder),
            5 => () => builder.AddKeyedDekafKafkaProducer(name, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void AddKeyedDekafKafkaProducerShouldThrowWhenConnectionNameIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;
        Action<KafkaProducerSettings>? configureSettings = null;
        Action<ProducerBuilder<string, string>>? configureBuilder = null;
        Action<IServiceProvider, ProducerBuilder<string, string>>? configureBuilderWithServiceProvider = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddKeyedDekafKafkaProducer<string, string>(name),
            1 => () => builder.AddKeyedDekafKafkaProducer<string, string>(name, configureSettings),
            2 => () => builder.AddKeyedDekafKafkaProducer(name, configureBuilder),
            3 => () => builder.AddKeyedDekafKafkaProducer(name, configureBuilderWithServiceProvider),
            4 => () => builder.AddKeyedDekafKafkaProducer(name, configureSettings, configureBuilder),
            5 => () => builder.AddKeyedDekafKafkaProducer(name, configureSettings, configureBuilderWithServiceProvider),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}

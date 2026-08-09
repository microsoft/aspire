# Aspire.Confluent.Kafka library

Provides ability to registers an [IProducer<TKey, TValue>](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.IProducer-2.html) and an [IConsumer<TKey, TValue>](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.IConsumer-2.html) in the DI container for producing and consuming messages to an Apache Kafka broker. Enables corresponding health check, logging and metrics.
This library wraps Confluent.Kafka binaries.

## Getting started

### Prerequisites

- An Apache Kafka broker.

### Install the package

Install the Aspire Confluent Kafka library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Confluent.Kafka
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddKafkaProducer` extension method to register an `IProducer<TKey, TValue>` for use via the dependency injection container. The method takes two generic parameters corresponding to the type of the key and the type of the message to send to the broker. These generic parameters will be used to new an instance of `ProducerBuilder<TKey, TValue>`. This method also take connection name parameter.

```csharp
builder.AddKafkaProducer<string, string>("messaging");
```

You can then retrieve the `IProducer<TKey, TValue>` instance using dependency injection. For example, to retrieve the producer from an `IHostedService`:

```csharp
internal sealed class MyWorker(IProducer<string, string> producer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        long i = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var message = new Message<string, string>
            {
              Key = Guid.NewGuid.ToString(),
              Value = $"Hello, World! {i}"
            };
            producer.Produce("topic", message);
            logger.LogInformation($"{producer.Name} sent message '{message.Value}'");
            i++;
        }
    }
}
```

You can refer to [Confluent's Apache Kafka .NET Client documentatoin](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html) for more information about how to use the `IProducer<TKey, TValue>` efficiently.

## Configuration

The Aspire Confluent Kafka component provides multiple options to configure the connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddKafkaProducer()` or `builder.AddKafkaProducer()`:

```csharp
builder.AddKafkaProducer<string, string>("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "broker:9092"
  }
}
```

The value provided as connection string will be set to the `BootstrapServers`  property of the produced `IProducer<TKey, TValue>` or `IConsumer<TKey, TValue>` instance. Refer to [BootstrapServers](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.ClientConfig.html#Confluent_Kafka_ClientConfig_BootstrapServers) for more information.

### Use configuration providers

The Aspire Confluent Kafka component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `KafkaProducerSettings` or `KafkaConsumerSettings` from configuration by respectively using the `Aspire:Confluent:Kafka:Producer` and `Aspire.Confluent:Kafka:Consumer` keys. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Confluent": {
      "Kafka": {
        "Producer": {
          "DisableHealthChecks": false,
          "Config": {
            "Acks": "All"
          }
        }
      }
    }
  }
}
```

The `Config` properties of both  `Aspire:Confluent:Kafka:Producer` and `Aspire.Confluent:Kafka:Consumer` configuration sections respectively bind to instances of [`ProducerConfig`](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.ProducerConfig.html) and [`ConsumerConfig`](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.ConsumerConfig.html).

`Confluent.Kafka.Consumer<TKey, TValue>` requires the `ClientId` property to be set to let the broker track consumed message offsets.

### Use inline delegates to configure `KafkaProducerSettings` and `KafkaConsumerSettings`.

Also you can pass the `Action<KafkaProducerSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddKafkaProducer<string, string>("messaging", settings => settings.DisableHealthChecks = true);
```

Similarly you can configure inline a consumer from code:
```c#
    builder.AddKafkaConsumer<string, string>("messaging", settings => settings.DisableHealthChecks = true);
```

### Use inline delegates to configure `ProducerBuilder<TKey, TValue>` and `ConsumerBuilder<TKey, TValue>`.

To configure `Confluent.Kafka` builders (for example to setup custom serializers/deserializers for message key and value) you can pass an `Action<ProducerBuilder<TKey, TValue>>` (or `Action<ConsumerBuilder<TKey, TValue>>`) from code:
```c#
    builder.AddKafkaProducer<string, MyMessage>("messaging", producerBuilder => {
      producerBuilder.SetValueSerializer(new MyMessageSerializer());
    })
```

You can refer to [`ProducerBuilder<TKey, TValue>`](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.ProducerBuilder-2.html) and [`ConsumerBuilder<TKey, TValue>`](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.ConsumerBuilder-2.html) api documentation for more information.

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Kafka` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Kafka
```

Then, in the _AppHost.cs_ file of `AppHost`, register an Apache Kafka container and consume the connection using the following methods:

```csharp
var messaging = builder.AddKafka("messaging");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(messaging);
```

The `WithReference` method configures a connection in the `MyService` project named `messaging`. In the _Program.cs_ file of `MyService`, the Apache Kafka broker connection can be consumed using:

```csharp
builder.AddKafkaProducer<string, string>("messaging");
```

or

```csharp
builder.AddKafkaConsumer<string, string>("messaging");
```

## Additional documentation

* https://docs.confluent.io/kafka-clients/dotnet/current/overview.html
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

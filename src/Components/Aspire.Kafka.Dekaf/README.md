# Aspire.Kafka.Dekaf library

Registers [Dekaf](https://thomhurst.github.io/Dekaf/) Kafka producers, consumers, and admin clients with dependency injection, logging, health checks, and OpenTelemetry tracing and metrics. Dekaf implements the Kafka protocol in managed .NET code and does not depend on Confluent.Kafka or librdkafka.

## Getting started

This integration requires .NET 10 or later and an Apache Kafka broker. Install the package in the application that sends or receives messages:

```dotnetcli
dotnet add package Aspire.Kafka.Dekaf
```

In the application's `Program.cs`:

```csharp
using Dekaf.Consumer;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddDekafKafkaProducer<string, string>("messaging");
builder.AddDekafKafkaConsumer<string, string>("messaging", consumer => consumer
    .WithGroupId("orders-service")
    .WithAutoOffsetReset(AutoOffsetReset.Earliest)
    .SubscribeTo("orders"));
builder.AddDekafKafkaAdminClient("messaging");

using var host = builder.Build();
await host.RunAsync();
```

Dekaf's hosted initialization service initializes producers and consumers during host startup, before application hosted services registered afterward. If you resolve a client without starting the host, call its `InitializeAsync(cancellationToken)` before using it. The host owns client disposal; application services should not dispose injected clients.

Inject `Dekaf.Producer.IKafkaProducer<TKey, TValue>`, `Dekaf.Consumer.IKafkaConsumer<TKey, TValue>`, or `Dekaf.Admin.IAdminClient`:

```csharp
await producer.ProduceAsync("orders", "order-123", "created", cancellationToken);

await foreach (var message in consumer.ConsumeAsync(cancellationToken))
{
    Console.WriteLine($"{message.Key}: {message.Value}");
}
```

Provision the topic before starting a consumer that subscribes to it. Admin clients support topic creation, cluster inspection, and the other operations exposed by Dekaf.

## AppHost integration

Use the existing `Aspire.Hosting.Kafka` package in the AppHost:

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var messaging = builder.AddKafka("messaging");

builder.AddProject<Projects.MyService>("my-service")
    .WithReference(messaging)
    .WaitFor(messaging);

builder.Build().Run();
```

`WithReference` supplies the `messaging` connection string to the application. The same Kafka resource works with Dekaf and Confluent clients.

## Configuration

A connection string is a comma-separated list of bootstrap servers:

```json
{
  "ConnectionStrings": {
    "messaging": "broker1:9092,broker2:9092"
  },
  "Aspire": {
    "Kafka": {
      "Dekaf": {
        "Producer": {
          "DisableHealthChecks": false,
          "Config": {
            "ClientId": "orders-producer",
            "Acks": "All",
            "LingerMs": 5
          }
        },
        "Consumer": {
          "Config": {
            "GroupId": "orders-service",
            "AutoOffsetReset": "Earliest"
          },
          "HealthCheck": {
            "Timeout": "00:00:05",
            "DegradedThreshold": 1000,
            "UnhealthyThreshold": 10000
          }
        },
        "AdminClient": {
          "HealthCheck": {
            "Timeout": "00:00:05"
          }
        }
      }
    }
  }
}
```

Settings are read from `Aspire:Kafka:Dekaf:Producer`, `Aspire:Kafka:Dekaf:Consumer`, and `Aspire:Kafka:Dekaf:AdminClient`. A subsection named after the connection overrides shared settings for both keyed and unkeyed registrations. For example, `Aspire:Kafka:Dekaf:Producer:messaging:Config:ClientId` overrides the shared producer client ID.

Configuration precedence, from lowest to highest:

1. Shared configuration section, including native `Config` options.
2. Named configuration subsection.
3. `ConnectionStrings:{connectionName}` overrides the configured connection string and native bootstrap servers.
4. `configureSettings` customizations, including `ConnectionString`.
5. `configureBuilder` customizations.

Producer and consumer `Config` sections use [Dekaf's native configuration names](https://thomhurst.github.io/Dekaf/docs/dependency-injection), not Confluent configuration names. Native `BootstrapServers` accepts either a comma-separated string or an array. The generated configuration schema describes common Aspire settings; native Dekaf options remain open because the schema generator does not support their init-only properties.

Use `configureSettings` for Aspire settings and `configureBuilder` for the full native builder surface:

```csharp
builder.AddDekafKafkaProducer<string, string>("messaging",
    configureSettings: settings => settings.DisableHealthChecks = true,
    configureBuilder: producer => producer
        .WithLinger(TimeSpan.FromMilliseconds(10))
        .WithSaslScramSha512(username, password)
        .UseTls());
```

Producer and consumer callbacks also have an `IServiceProvider` overload for serializers, deserializers, interceptors, and other application dependencies:

```csharp
builder.AddDekafKafkaProducer<string, Order>("messaging",
    configureBuilder: (services, producer) => producer
        .WithValueSerializer(services.GetRequiredService<OrderSerializer>()));
```

Admin clients accept a native `AdminClientBuilder` callback:

```csharp
builder.AddDekafKafkaAdminClient("messaging",
    configureBuilder: (services, admin) => admin
        .WithSaslScramSha512(username, password)
        .UseTls());
```

Configure the admin client's security settings for the same cluster when using its connectivity check alongside producer or consumer checks. Credentials can come from a secret configuration provider or a service resolved by the callback.

This integration uses Dekaf's reflection-based configuration binding. Its registration methods are annotated with `RequiresDynamicCode` and `RequiresUnreferencedCode`; trimmed and Native AOT applications are not currently supported by this integration. Dekaf itself offers separate programmatic registration APIs for those scenarios.

## Keyed registrations

Use keyed registrations for multiple clusters or independently configured clients of the same type:

```csharp
using Dekaf.Producer;
using Microsoft.Extensions.DependencyInjection;

builder.AddKeyedDekafKafkaProducer<string, string>("orders");
builder.AddKeyedDekafKafkaConsumer<string, string>("orders", consumer => consumer
    .WithGroupId("orders-service")
    .SubscribeTo("orders"));
builder.AddKeyedDekafKafkaAdminClient("orders");

// Resolve after starting the host.
var producer = host.Services.GetRequiredKeyedService<IKafkaProducer<string, string>>("orders");
```

The name is both the service key and connection string name. Each keyed registration receives its own health check.

## Health checks

Health checks are enabled by default and can be disabled with `DisableHealthChecks`:

| Registration | Check name | What it checks |
| --- | --- | --- |
| Producer | `Kafka.Dekaf_producer` | A producer flush checkpoint completes within the timeout. |
| Consumer | `Kafka.Dekaf_consumer` | Consumer group liveness and lag for assigned partitions. |
| Admin client | `Kafka.Dekaf_admin` | An active cluster-description request returns at least one broker. |

Keyed check names append `_{name}`. Check timeouts default to five seconds and can be configured through `HealthCheck.Timeout`.

A successful producer flush does **not** prove broker connectivity or successful message delivery. An idle producer can pass its flush check while brokers are unavailable. Register the admin client for active connectivity monitoring and observe produce results for delivery success. Unlike the Confluent integration's health check, these checks do not publish synthetic messages to a health-check topic.

The consumer check reports unhealthy before it has an assignment or live group membership. A live group member with no assigned partitions is a healthy standby by default. Consumer `HealthCheck` options also expose lag thresholds and `NoAssignmentStatus`. See [Dekaf health checks](https://thomhurst.github.io/Dekaf/docs/health-checks) for the precise guarantees.

## Observability

Dekaf writes logs through the application's `ILoggerFactory` under `Dekaf.*` categories. This integration registers the `Dekaf` activity source and meter with OpenTelemetry. Configure exporters through the application's service defaults or OpenTelemetry configuration.

Tracing includes producer spans and consumer spans linked to the producing trace. Metrics include sent and consumed message counts, operation duration, retries, bytes, consumer lag, and internal producer/consumer measurements. See [Dekaf observability](https://thomhurst.github.io/Dekaf/docs/observability).

`DisableTracing` and `DisableMetrics` prevent a registration from subscribing OpenTelemetry to the corresponding source or meter. Dekaf shares one source and meter across all clients, so another enabled registration or an application listener can still collect telemetry from those clients.

## Migrating from Aspire.Confluent.Kafka

Replace the package reference and registration methods with `AddDekafKafkaProducer` and `AddDekafKafkaConsumer`, then inject Dekaf's client interfaces. The `Dekaf` prefix lets both integrations coexist during migration. The connection string and AppHost Kafka resource can remain unchanged.

Update native configuration and serialization callbacks for Dekaf. Compression codecs and schema registry support are available through optional Dekaf packages. Transactions, partition assignment, and other native features remain available through the clients and builder callbacks. Review [Dekaf's migration guide](https://thomhurst.github.io/Dekaf/docs/migrating-from-confluent-kafka), particularly its offset-storage and delivery-semantics differences.

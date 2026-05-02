# Aspire.Hosting.RabbitMQ library

Provides extension methods and resource definitions for an Aspire AppHost to configure a RabbitMQ resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire RabbitMQ Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.RabbitMQ
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a RabbitMQ resource and consume the connection using the following methods:

```csharp
var rmq = builder.AddRabbitMQ("rmq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(rmq);
```

## Virtual hosts, queues, exchanges, bindings, and shovels

You can declare RabbitMQ topology as first-class Aspire resources. Topology is provisioned automatically after the container is healthy, and `WaitFor(child)` blocks until the child resource is fully applied.

### Virtual hosts

```csharp
var rmq = builder.AddRabbitMQ("rmq");

// Add a named virtual host (auto-enables the management plugin)
var orders = rmq.AddVirtualHost("orders");

// Add queue and exchange on the virtual host
var inbox  = orders.AddQueue("inbox");
var events = orders.AddExchange("events", RabbitMQExchangeType.Topic);

// Bind the exchange to the queue
events.WithBinding(inbox, routingKey: "order.*");

// Reference the queue from a service — connection string includes the vhost segment
builder.AddProject<Projects.OrdersApi>("api")
       .WithReference(inbox);
```

### Server-level convenience overloads (default `/` vhost)

```csharp
var rmq = builder.AddRabbitMQ("rmq");

// These create resources on the default "/" virtual host
var queue    = rmq.AddQueue("my-queue");
var exchange = rmq.AddExchange("my-exchange", RabbitMQExchangeType.Fanout);
```

### Shovels

Shovels move messages between queues or exchanges, including across virtual hosts:

```csharp
var rmq     = builder.AddRabbitMQ("rmq");
var orders  = rmq.AddVirtualHost("orders");
var billing = rmq.AddVirtualHost("billing");

var ordersInbox  = orders.AddQueue("inbox");
var billingInbox = billing.AddQueue("inbox");

// Shovel from orders/inbox → billing/inbox (auto-enables shovel plugins)
orders.AddShovel("orders-to-billing", source: ordersInbox, destination: billingInbox);

// WaitFor blocks until the shovel is running
builder.AddProject<Projects.BillingWorker>("worker")
       .WaitFor(billingInbox);
```

### Queue and exchange properties

```csharp
var rmq   = builder.AddRabbitMQ("rmq");
var vhost = rmq.AddVirtualHost("orders");

var queue = vhost.AddQueue("inbox", type: RabbitMQQueueType.Quorum)
                 .WithProperties(q =>
                 {
                     q.Durable    = true;
                     q.AutoDelete = false;
                 });

var exchange = vhost.AddExchange("events", RabbitMQExchangeType.Topic)
                    .WithProperties(e => e.Durable = true);
```

## Plugin customization

Use `WithPlugin` to enable additional RabbitMQ plugins. The management-image default set (`rabbitmq_management`, `rabbitmq_management_agent`, `rabbitmq_web_dispatch`, `rabbitmq_prometheus`) is always included so behaviour never regresses.

```csharp
var rmq = builder.AddRabbitMQ("rmq")
                 .WithPlugin(RabbitMQPlugin.Prometheus)   // enum overload
                 .WithPlugin("rabbitmq_mqtt");             // string overload
```

Plugins are automatically enabled when child resources require them:

| Action | Auto-enabled plugins |
|---|---|
| `AddVirtualHost("name")` (non-`/`) | `rabbitmq_management` |
| `AddShovel(...)` | `rabbitmq_management`, `rabbitmq_shovel`, `rabbitmq_shovel_management` |

## Health checks

Every RabbitMQ child resource registers its own health check. The Aspire dashboard shows each resource's status independently, and `WaitFor(resource)` blocks until that specific resource is healthy — meaning it was provisioned successfully and a live broker probe confirms it still exists.

Failures are isolated: if one queue fails to declare, only that queue's health check reports `Unhealthy`. Sibling queues, exchanges, and shovels are unaffected. The only cascade is a vhost-creation failure, which marks every child in that vhost as `Unhealthy`, because nothing can exist without the vhost.

Bindings are owned by the source exchange. If a binding fails, the exchange is `Unhealthy`; the destination queue is not affected.

## Connection Properties

When you reference a RabbitMQ resource using `WithReference`, the following connection properties are made available to the consuming project:

### RabbitMQ server

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the RabbitMQ server |
| `Port` | The port number the RabbitMQ server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `amqp://{Username}:{Password}@{Host}:{Port}` |

### RabbitMQ virtual host

Inherits all server properties, plus:

| Property Name | Description |
|---------------|-------------|
| `VirtualHost` | The name of the virtual host |
| `Uri` | The connection URI including the vhost segment, e.g. `amqp://user:pass@host:port/orders` |

### RabbitMQ queue

Inherits all virtual host properties, plus:

| Property Name | Description |
|---------------|-------------|
| `QueueName` | The name of the queue |

### RabbitMQ exchange

Inherits all virtual host properties, plus:

| Property Name | Description |
|---------------|-------------|
| `ExchangeName` | The name of the exchange |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `inbox` becomes `INBOX_URI`.

## Additional documentation

* https://aspire.dev/integrations/messaging/rabbitmq/

## Feedback & contributing

https://github.com/microsoft/aspire

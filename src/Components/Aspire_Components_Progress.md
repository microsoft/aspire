# Aspire Client Integrations Progress

These integrations should follow the [Aspire Client Integration Requirements](#aspire-client-integration-requirements). Below is a chart that shows the progress of each of the client integrations we ship, and their current stance against each of the requirements.

| Aspire Client Integration Name              | [Contains README](#contains-readme) | [Public API](#public-api) | [Configuration Schema](#json-schemaconfiguration) | [DI Services](#di-services) | [Logging](#logging) | [Tracing](#tracing) | [Metrics](#metrics) | [Health Checks](#health-checks) |
| --------------------------------------- | :---------------------------------: | :-----------------------: | :----------------------------------------------------: | :-------------------------: | :-----------------: | :-----------------: | :-----------------: | :-----------------------------: |
| Npgsql                                  |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| Npgsql.EntityFrameworkCore.PostgreSQL   |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| Microsoft.Azure.Cosmos                  |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ❌                  |
| Microsoft.Data.SqlClient                |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ❌          |        ✅            |         ❌           |              ✅                  |
| Microsoft.EntityFramework.Cosmos        |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ❌                  |
| Microsoft.EntityFrameworkCore.SqlServer |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Microsoft.Extensions.Configuration.AzureAppConfiguration | ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |                                  |
| MongoDB.Driver                          |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| MongoDB.EntityFrameworkCore             |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.AI.Inference                      |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ❌                  |
| Azure.AI.OpenAI                         |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ❌                  |
| Azure.Data.Tables                       |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Messaging.EventHubs               |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |                                  |
| Azure.Messaging.WebPubSub               |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Messaging.ServiceBus              |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Npgsql                            |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| Azure.Npgsql.EntityFrameworkCore.PostgreSQL |              ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| Azure.Search.Documents                  |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Security.KeyVault                 |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Storage.Blobs                     |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Azure.Storage.Queues                    |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| StackExchange.Redis                     |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| StackExchange.Redis.DistributedCaching  |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| StackExchange.Redis.OutputCaching       |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| RabbitMQ                                |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |                      |         ❌           |              ✅                  |
| MySqlConnector                          |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| OpenAI                                  |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ❌          |        ✅            |         ✅           |              ❌                  |
| Oracle.EntityFrameworkCore              |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ❌           |              ✅                  |
| Confluent.Kafka                         |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ❌            |         ✅           |              ✅                  |
| Pomelo.EntityFrameworkCore.MySql        |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |         ✅           |              ✅                  |
| NATS.Net                                |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        ✅            |                      |              ✅                  |
| Seq                                     |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |        N/A           |        N/A           |              ✅                  |
| Qdrant.Client                           |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |                    |                   |                                |
| Milvus.Client                           |                  ✅                  |             ✅             |                           ✅                            |              ✅              |          ✅          |                    |                   |                    ✅          |
| Elastic.Clients.Elasticsearch           |                  ✅                  |             ✅             |                           ✅                            |              ✅              |                      |         ✅           |                       |              ✅                 |

Nomenclature used in the table above:

- ✅ - Requirement is met
- (blank) - Requirement hasn't been met yet
- ❌ - Requirement can't be met
- N/A - Requirement not applicable

## Aspire Client Integration Requirements

### Contains README

Each Aspire client integration must contain a README.md file which is included in the package. This README should contain the client integration's main description, usage examples, and basic getting started documentation. The goal of this file is to contain everything a developer will need in the first 5 minutes. Finally, README should have a link pointed back to the full documentation of the client integration, which will include a list of logging categories used, tracing activity names, and Metric names. [Here's a concrete example of a README file](./Aspire.StackExchange.Redis/README.md).

### Public API

Each client integration must go through an API Review that will validate that the API shape proposed by the client integration is conforming to the guidelines. This also includes the [naming conventions](./README.md#naming).

### Json Schema/Configuration

Each client integration should provide a `sealed` `Settings` type as well as named configuration which will showcase the exposed settings for that specific client integration. This will also include the Logging section in which categories are listed and can be configured. For more information, please check out [the configuration best practices](./README.md#configuration)

### DI Services

Client integrations must have extension methods which will "glue" the services with the DI container. For an example of this, please check out the `AspireRedisExtensions` [class here](./Aspire.StackExchange.Redis/AspireRedisExtensions.cs). The extension methods that are registering the main client integration's service to the container should be listed in the README file. The full list of registered services by the client integration should be included in the main client integration's documentation page.

### Health Checks

Aspire client integrations should expose health checks enabling applications to track and respond to the remote service’s health. For more information, please check out [the health checks best practices](./README.md#health-checks).

### Telemetry

#### Logging

Client integrations should produce Logs as part of them being cloud-ready. As part of the client integration's main documentation, it's a requirement to list out the different categories that it uses for logging. The list of categories doesn't need to be exhaustive, but it should include at least the logical level groupings used.

#### Tracing

Integrations should also produce tracing information as part of them being cloud-ready. As part of the client integration's main documentation, it's a requirement to list out the different activity names that it uses for tracing. It is not a requirement to include tag names.

#### Metrics

Integrations should also produce metrics as part of them being cloud-ready. As part of the client integration's main documentation, it's a requirement to list out the different metric names that it uses. It is not a requirement to also include dimensions.

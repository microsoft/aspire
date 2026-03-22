# Log categories, activity source names and metric names

Aspire.Azure.AI.Inference:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "Azure.AI.Inference.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.AI.OpenAI:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "OpenAI.*"
- Metric names:
  - "OpenAI.*"

Aspire.Azure.Data.Tables:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "Azure.Data.Tables.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Messaging.EventHubs:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
  - "Azure.Messaging.EventHubs"
- Activity source names:
  - "Azure.Messaging.EventHubs.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

  - Aspire.Azure.Messaging.ServiceBus:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
  - "Azure.Messaging.ServiceBus"
- Activity source names:
  - "Azure.Messaging.ServiceBus.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Messaging.WebPubSub:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
  - "Azure.Messaging.WebPubSub"
- Activity source names:
  - "Azure.Messaging.WebPubSub.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Npgsql:
- Everything from `Aspire.Npgsql`

Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL:
- Everything from `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`

Aspire.Azure.Search.Documents:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
  - "Azure-Search-Documents"
- Activity source names:
  - "Azure.Search.Documents.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Security.KeyVault:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "Azure.Security.KeyVault.Secrets.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Storage.Blobs:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "Azure.Storage.Blobs.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Azure.Storage.Queues:
- Log categories:
  - "Azure.Core"
  - "Azure.Identity"
- Activity source names:
  - "Azure.Storage.Queues.*"
- Metric names:
  - none (currently not supported by the Azure SDK)

Aspire.Confluent.Kafka:
- Log categories:
  - "Aspire.Confluent.Kafka"
- Activity source names:
  - "OpenTelemetry.Instrumentation.ConfluentKafka"
- Metric names:
  - "Aspire.Confluent.Kafka"
    - "messaging.kafka.consumer.queue.message_count"
    - "messaging.kafka.producer.queue.message_count"
    - "messaging.kafka.producer.queue.size"
    - "messaging.kafka.network.tx"
    - "messaging.kafka.network.transmitted"
    - "messaging.kafka.network.rx"
    - "messaging.kafka.network.received"
    - "messaging.kafka.message.tx"
    - "messaging.kafka.message.transmitted"
    - "messaging.kafka.message.rx"
    - "messaging.kafka.message.received"
  - "OpenTelemetry.Instrumentation.ConfluentKafka"
    - "messaging.publish.duration"
    - "messaging.publish.messages"
    - "messaging.receive.duration"
    - "messaging.receive.messages"

Aspire.Elastic.Clients.Elasticsearch:
- Log categories:
  - none (not currently supported by Elastic.Clients.Elasticsearch library)
- Activity source names:
  - "Elastic.Transport"
- Metric names:
  - none

Aspire.Microsoft.Azure.Cosmos:
- Log categories:
  - "Azure-Cosmos-Operation-Request-Diagnostics"
- Activity source names:
  - "Azure.Cosmos.Operation"
- Metric names:
  - none

Aspire.Microsoft.Data.SqlClient:
- Log categories:
  - none (the client does not provide an easy way to integrate it with logger factory)
- Activity source names:
  - "OpenTelemetry.Instrumentation.SqlClient"
- Metric names:
  - none

Aspire.MongoDB.Driver:
- Log categories:
  - "MongoDB"
  - "MongoDB.Command"
  - "MongoDB.SDAM"
  - "MongoDB.ServerSelection"
  - "MongoDB.Connection"
  - "MongoDB.Internal"
- Activity source names:
  - "MongoDB.Driver.Core.Extensions.DiagnosticSources"
- Metric names:
  - none

Aspire.MongoDB.EntityFrameworkCore:
- Log categories:
  - "Microsoft.EntityFrameworkCore.ChangeTracking"
  - "Microsoft.EntityFrameworkCore.Database.Command"
  - "Microsoft.EntityFrameworkCore.Infrastructure"
  - "Microsoft.EntityFrameworkCore.Model"
  - "Microsoft.EntityFrameworkCore.Model.Validation"
  - "Microsoft.EntityFrameworkCore.Query"
  - "Microsoft.EntityFrameworkCore.Update"
- Activity source names:
  - "MongoDB.Driver.Core.Extensions.DiagnosticSources"
- Metric names:
  - none

Aspire.Microsoft.EntityFrameworkCore.Cosmos:
- Log categories:
  - "Azure-Cosmos-Operation-Request-Diagnostics"
  - "Microsoft.EntityFrameworkCore.ChangeTracking",
  - "Microsoft.EntityFrameworkCore.Database.Command",
  - "Microsoft.EntityFrameworkCore.Infrastructure",
  - "Microsoft.EntityFrameworkCore.Query",
- Activity source names:
  - "Azure.Cosmos.Operation"
- Metric names:
  - none

Aspire.Microsoft.EntityFrameworkCore.SqlServer:
- Log categories:
  - "Microsoft.EntityFrameworkCore.ChangeTracking"
  - "Microsoft.EntityFrameworkCore.Database.Command"
  - "Microsoft.EntityFrameworkCore.Database.Connection"
  - "Microsoft.EntityFrameworkCore.Database.Transaction"
  - "Microsoft.EntityFrameworkCore.Infrastructure"
  - "Microsoft.EntityFrameworkCore.Migrations"
  - "Microsoft.EntityFrameworkCore.Model"
  - "Microsoft.EntityFrameworkCore.Model.Validation"
  - "Microsoft.EntityFrameworkCore.Query"
  - "Microsoft.EntityFrameworkCore.Update"
- Activity source names:
  - "OpenTelemetry.Instrumentation.SqlClient"
- Metric names:
  - none

Aspire.Microsoft.Extensions.Configuration.AzureAppConfiguration
- Log categories:
  "Microsoft.Extensions.Configuration.AzureAppConfiguration.Refresh"
- Activity source names:
  - "Microsoft.Extensions.Configuration.AzureAppConfiguration"
- Metric names:
  - none

Aspire.Milvus.Client:
- Log categories:
  "Milvus.Client"
- Activity source names:
  - none (not currently supported by Milvus.Client library)
- Metric names:
  - none (currently not supported by Milvus.Client library)

Aspire.MySqlConnector:
- Log categories:
  - "MySqlConnector.ConnectionPool"
  - "MySqlConnector.MySqlBulkCopy"
  - "MySqlConnector.MySqlCommand"
  - "MySqlConnector.MySqlConnection"
  - "MySqlConnector.MySqlDataSource"
- Activity source names:
  - "MySqlConnector"
- Metric names:
  - "MySqlConnector":
    - "db.client.connections.create_time"
    - "db.client.connections.use_time"
    - "db.client.connections.wait_time"
    - "db.client.connections.idle.max"
    - "db.client.connections.idle.min"
    - "db.client.connections.max"
    - "db.client.connections.pending_requests"
    - "db.client.connections.timeouts"
    - "db.client.connections.usage"

Aspire.NATS.Net:
- Log categories:
  - "NATS"
- Activity source names:
  - "NATS.Net"
- Metric names:
  - none (currently not supported by NATS.Net library)

Aspire.Npgsql:
- Log categories:
  - "Npgsql.Command"
  - "Npgsql.Connection"
  - "Npgsql.Copy"
  - "Npgsql.Exception"
  - "Npgsql.Replication"
  - "Npgsql.Transaction"
- Activity source names:
  - "Npgsql"
- Metric names:
  - "Npgsql":
    - "db.client.commands.bytes_read"
    - "db.client.commands.bytes_written"
    - "db.client.commands.duration"
    - "db.client.commands.executing"
    - "db.client.commands.failed"
    - "db.client.connections.create_time"
    - "db.client.connections.max"
    - "db.client.connections.pending_requests"
    - "db.client.connections.timeouts"
    - "db.client.connections.usage"

Aspire.Npgsql.EntityFrameworkCore.PostgreSQL:
- Log categories:
  - "Microsoft.EntityFrameworkCore.ChangeTracking"
  - "Microsoft.EntityFrameworkCore.Database.Command"
  - "Microsoft.EntityFrameworkCore.Database.Connection"
  - "Microsoft.EntityFrameworkCore.Database.Transaction"
  - "Microsoft.EntityFrameworkCore.Infrastructure"
  - "Microsoft.EntityFrameworkCore.Migrations"
  - "Microsoft.EntityFrameworkCore.Model"
  - "Microsoft.EntityFrameworkCore.Model.Validation"
  - "Microsoft.EntityFrameworkCore.Query"
  - "Microsoft.EntityFrameworkCore.Update"
- Activity source names:
  - "Npgsql"
- Metric names:
  - "Npgsql":
    - "db.client.commands.bytes_read"
    - "db.client.commands.bytes_written"
    - "db.client.commands.duration"
    - "db.client.commands.executing"
    - "db.client.commands.failed"
    - "db.client.connections.create_time"
    - "db.client.connections.max"
    - "db.client.connections.pending_requests"
    - "db.client.connections.timeouts"
    - "db.client.connections.usage"

Aspire.OpenAI:
- Log categories:
  - none
- Activity source names:
  - "OpenAI.*"
- Metric names:
  - "OpenAI.*"

Aspire.Oracle.EntityFrameworkCore:
- Log categories:
  - "Microsoft.EntityFrameworkCore.ChangeTracking"
  - "Microsoft.EntityFrameworkCore.Database.Command"
  - "Microsoft.EntityFrameworkCore.Database.Connection"
  - "Microsoft.EntityFrameworkCore.Database.Transaction"
  - "Microsoft.EntityFrameworkCore.Infrastructure"
  - "Microsoft.EntityFrameworkCore.Migrations"
  - "Microsoft.EntityFrameworkCore.Model"
  - "Microsoft.EntityFrameworkCore.Model.Validation"
  - "Microsoft.EntityFrameworkCore.Query"
  - "Microsoft.EntityFrameworkCore.Update"
- Activity source names:
  - "Oracle.ManagedDataAccess.Core"
- Metric names:
  - none

Aspire.Pomelo.EntityFrameworkCore.MySql:
- Log categories:
  - "Microsoft.EntityFrameworkCore.ChangeTracking"
  - "Microsoft.EntityFrameworkCore.Database.Command"
  - "Microsoft.EntityFrameworkCore.Database.Connection"
  - "Microsoft.EntityFrameworkCore.Database.Transaction"
  - "Microsoft.EntityFrameworkCore.Infrastructure"
  - "Microsoft.EntityFrameworkCore.Migrations"
  - "Microsoft.EntityFrameworkCore.Model"
  - "Microsoft.EntityFrameworkCore.Model.Validation"
  - "Microsoft.EntityFrameworkCore.Query"
  - "Microsoft.EntityFrameworkCore.Update"
- Activity source names:
  - "MySqlConnector"
- Metric names:
  - "MySqlConnector":
    - "db.client.connections.create_time"
    - "db.client.connections.use_time"
    - "db.client.connections.wait_time"
    - "db.client.connections.idle.max"
    - "db.client.connections.idle.min"
    - "db.client.connections.max"
    - "db.client.connections.pending_requests"
    - "db.client.connections.timeouts"
    - "db.client.connections.usage"

Aspire.Qdrant.Client:
- Log categories:
  "Qdrant.Client"
- Activity source names:
  - none (not currently supported by Qdrant.Client library)
- Metric names:
  - none (currently not supported by Qdrant.Client library)

Aspire.RabbitMQ.Client:
- Log categories:
  - "RabbitMQ.Client"
- Activity source names:
  - "Aspire.RabbitMQ.Client"
- Metric names:
  - none (currently not supported by RabbitMQ.Client library)

Aspire.Seq:
- Log categories:
  - "Seq"
- Activity source names:
  - N/A (Seq is a telemetry sink, not a telemetry source)
- Metric names:
  - N/A (Seq is a telemetry sink, not a telemetry source)

Aspire.StackExchange.Redis:
- Log categories:
  - "StackExchange.Redis"
- Activity source names:
  - "OpenTelemetry.Instrumentation.StackExchangeRedis"
- Metric names:
  - none (currently not supported by StackExchange.Redis library)

Aspire.StackExchange.Redis.DistributedCaching:
- Everything from `Aspire.StackExchange.Redis` plus:
- Log categories:
  - "Microsoft.Extensions.Caching.StackExchangeRedis"

Aspire.StackExchange.Redis.OutputCaching:
- Everything from `Aspire.StackExchange.Redis` plus:
- Log categories:
  - "Microsoft.AspNetCore.OutputCaching.StackExchangeRedis"

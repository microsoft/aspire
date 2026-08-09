// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

namespace OpenTelemetry.Trace;

/// <summary>
/// Constants for semantic attribute names outlined by the OpenTelemetry specifications.
/// <see href="https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/README.md"/>.
/// </summary>
internal static class SemanticConventions
{
    // The set of constants matches the specification as of this commit.
    // https://github.com/open-telemetry/opentelemetry-specification/tree/main/specification/trace/semantic_conventions
    // https://github.com/open-telemetry/semantic-conventions/blob/main/docs/exceptions/exceptions-spans.md
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public const string AttributeNetTransport = "net.transport";
    public const string AttributeNetPeerIp = "net.peer.ip";
    public const string AttributeNetPeerPort = "net.peer.port";
    public const string AttributeNetPeerName = "net.peer.name";
    public const string AttributeNetHostIp = "net.host.ip";
    public const string AttributeNetHostPort = "net.host.port";
    public const string AttributeNetHostName = "net.host.name";

    public const string AttributeEnduserId = "enduser.id";
    public const string AttributeEnduserRole = "enduser.role";
    public const string AttributeEnduserScope = "enduser.scope";

    public const string AttributeHttpMethod = "http.method";
    public const string AttributeHttpUrl = "http.url";
    public const string AttributeHttpTarget = "http.target";
    public const string AttributeHttpHost = "http.host";
    public const string AttributeHttpScheme = "http.scheme";
    public const string AttributeHttpStatusCode = "http.status_code";
    public const string AttributeHttpStatusText = "http.status_text";
    public const string AttributeHttpFlavor = "http.flavor";
    public const string AttributeHttpServerName = "http.server_name";
    public const string AttributeHttpRoute = "http.route";
    public const string AttributeHttpClientIP = "http.client_ip";
    public const string AttributeHttpUserAgent = "http.user_agent";
    public const string AttributeHttpRequestContentLength = "http.request_content_length";
    public const string AttributeHttpRequestContentLengthUncompressed = "http.request_content_length_uncompressed";
    public const string AttributeHttpResponseContentLength = "http.response_content_length";
    public const string AttributeHttpResponseContentLengthUncompressed = "http.response_content_length_uncompressed";

    public const string AttributeDbConnectionString = "db.connection_string";
    public const string AttributeDbUser = "db.user";
    public const string AttributeDbMsSqlInstanceName = "db.mssql.instance_name";
    public const string AttributeDbJdbcDriverClassName = "db.jdbc.driver_classname";
    public const string AttributeDbName = "db.name";
    public const string AttributeDbStatement = "db.statement";
    public const string AttributeDbSystem = "db.system";
    public const string AttributeDbOperation = "db.operation";
    public const string AttributeDbInstance = "db.instance";
    public const string AttributeDbCassandraKeyspace = "db.cassandra.keyspace";
    public const string AttributeDbHBaseNamespace = "db.hbase.namespace";
    public const string AttributeDbRedisDatabaseIndex = "db.redis.database_index";
    public const string AttributeDbMongoDbCollection = "db.mongodb.collection";

    public const string AttributeRpcSystem = "rpc.system";
    public const string AttributeRpcService = "rpc.service";
    public const string AttributeRpcMethod = "rpc.method";
    public const string AttributeRpcGrpcStatusCode = "rpc.grpc.status_code";
    public const string AttributeRpcResponseStatusCode = "rpc.response.status_code";

    public const string AttributeMessageType = "message.type";
    public const string AttributeMessageId = "message.id";
    public const string AttributeMessageCompressedSize = "message.compressed_size";
    public const string AttributeMessageUncompressedSize = "message.uncompressed_size";

    public const string AttributeFaasTrigger = "faas.trigger";
    public const string AttributeFaasExecution = "faas.execution";
    public const string AttributeFaasDocumentCollection = "faas.document.collection";
    public const string AttributeFaasDocumentOperation = "faas.document.operation";
    public const string AttributeFaasDocumentTime = "faas.document.time";
    public const string AttributeFaasDocumentName = "faas.document.name";
    public const string AttributeFaasTime = "faas.time";
    public const string AttributeFaasCron = "faas.cron";

    public const string AttributeMessagingSystem = "messaging.system";
    public const string AttributeMessagingDestination = "messaging.destination";
    public const string AttributeMessagingDestinationKind = "messaging.destination_kind";
    public const string AttributeMessagingTempDestination = "messaging.temp_destination";
    public const string AttributeMessagingProtocol = "messaging.protocol";
    public const string AttributeMessagingProtocolVersion = "messaging.protocol_version";
    public const string AttributeMessagingUrl = "messaging.url";
    public const string AttributeMessagingMessageId = "messaging.message_id";
    public const string AttributeMessagingConversationId = "messaging.conversation_id";
    public const string AttributeMessagingPayloadSize = "messaging.message_payload_size_bytes";
    public const string AttributeMessagingPayloadCompressedSize = "messaging.message_payload_compressed_size_bytes";
    public const string AttributeMessagingOperation = "messaging.operation";

    public const string AttributeExceptionEventName = "exception";
    public const string AttributeExceptionType = "exception.type";
    public const string AttributeExceptionMessage = "exception.message";
    public const string AttributeExceptionStacktrace = "exception.stacktrace";
    public const string AttributeErrorType = "error.type";

    // v1.21.0
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.21.0/docs/http/http-metrics.md#http-server
    public const string AttributeHttpRequestMethod = "http.request.method"; // replaces: "http.method" (AttributeHttpMethod)
    public const string AttributeHttpRequestMethodOriginal = "http.request.method_original";
    public const string AttributeHttpResponseStatusCode = "http.response.status_code"; // replaces: "http.status_code" (AttributeHttpStatusCode)
    public const string AttributeUrlScheme = "url.scheme"; // replaces: "http.scheme" (AttributeHttpScheme)
    public const string AttributeUrlFull = "url.full"; // replaces: "http.url" (AttributeHttpUrl)
    public const string AttributeUrlPath = "url.path"; // replaces: "http.target" (AttributeHttpTarget)
    public const string AttributeUrlQuery = "url.query"; // replaces: "http.target" (AttributeHttpTarget)
    public const string AttributeServerSocketAddress = "server.socket.address"; // replaces: "net.peer.ip" (AttributeNetPeerIp)

    // v1.23.0
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.23.0/docs/http/http-metrics.md#http-server
    public const string AttributeClientAddress = "client.address";
    public const string AttributeClientPort = "client.port";
    public const string AttributeNetworkProtocolVersion = "network.protocol.version"; // replaces: "http.flavor" (AttributeHttpFlavor)
    public const string AttributeNetworkProtocolName = "network.protocol.name";
    public const string AttributeServerAddress = "server.address"; // replaces: "net.host.name" (AttributeNetHostName)
    public const string AttributeServerPort = "server.port"; // replaces: "net.host.port" (AttributeNetHostPort)
    public const string AttributeUserAgentOriginal = "user_agent.original"; // replaces: http.user_agent (AttributeHttpUserAgent)

    // v1.23.0 Database spans
    // https://github.com/open-telemetry/semantic-conventions/blob/release/v1.23.x/docs/database/database-spans.md
    public const string AttributeNetworkPeerAddress = "network.peer.address"; // replaces: "net.peer.ip" (AttributeNetPeerIp)
    public const string AttributeNetworkPeerPort = "network.peer.port"; // replaces: "net.peer.port" (AttributeNetPeerPort)

    // v1.24.0 Messaging spans
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.24.0/docs/messaging/messaging-spans.md
    public const string AttributeMessagingClientId = "messaging.client_id";
    public const string AttributeMessagingDestinationName = "messaging.destination.name";

    // v1.24.0 Messaging metrics
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.24.0/docs/messaging/messaging-metrics.md
    public const string MetricMessagingPublishDuration = "messaging.publish.duration";
    public const string MetricMessagingPublishMessages = "messaging.publish.messages";
    public const string MetricMessagingReceiveDuration = "messaging.receive.duration";
    public const string MetricMessagingReceiveMessages = "messaging.receive.messages";

    // v1.24.0 Messaging (Kafka)
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.24.0/docs/messaging/kafka.md
    public const string AttributeMessagingKafkaConsumerGroup = "messaging.kafka.consumer.group";
    public const string AttributeMessagingKafkaDestinationPartition = "messaging.kafka.destination.partition";
    public const string AttributeMessagingKafkaMessageKey = "messaging.kafka.message.key";
    public const string AttributeMessagingKafkaMessageOffset = "messaging.kafka.message.offset";

    // v1.36.0 database conventions:
    // https://github.com/open-telemetry/semantic-conventions/tree/v1.36.0/docs/database
    public const string AttributeDbCollectionName = "db.collection.name";
    public const string AttributeDbOperationName = "db.operation.name";
    public const string AttributeDbSystemName = "db.system.name";
    public const string AttributeDbNamespace = "db.namespace";
    public const string AttributeDbResponseStatusCode = "db.response.status_code";
    public const string AttributeDbOperationBatchSize = "db.operation.batch.size";
    public const string AttributeDbQuerySummary = "db.query.summary";
    public const string AttributeDbQueryText = "db.query.text";
    public const string AttributeDbStoredProcedureName = "db.stored_procedure.name";

    // v1.40.0 RPC spans
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.40.0/docs/rpc/rpc-spans.md
    public const string AttributeRpcSystemName = "rpc.system.name";

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}

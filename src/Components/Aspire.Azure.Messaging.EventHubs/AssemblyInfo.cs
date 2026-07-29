// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Azure.Messaging.EventHubs.Producer;

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubConsumerClient", typeof(AzureMessagingEventHubsConsumerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubConsumerClient:ClientOptions", typeof(EventHubConsumerClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubConsumerClient:*", typeof(AzureMessagingEventHubsConsumerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubConsumerClient:*:ClientOptions", typeof(EventHubConsumerClientOptions))]

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubProducerClient", typeof(AzureMessagingEventHubsProducerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubProducerClient:ClientOptions", typeof(EventHubProducerClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubProducerClient:*", typeof(AzureMessagingEventHubsProducerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubProducerClient:*:ClientOptions", typeof(EventHubProducerClientOptions))]

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventProcessorClient", typeof(AzureMessagingEventHubsProcessorSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventProcessorClient:ClientOptions", typeof(EventProcessorClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventProcessorClient:*", typeof(AzureMessagingEventHubsProcessorSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventProcessorClient:*:ClientOptions", typeof(EventProcessorClientOptions))]

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:PartitionReceiver", typeof(AzureMessagingEventHubsPartitionReceiverSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:PartitionReceiver:ClientOptions", typeof(PartitionReceiverOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:PartitionReceiver:*", typeof(AzureMessagingEventHubsPartitionReceiverSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:PartitionReceiver:*:ClientOptions", typeof(PartitionReceiverOptions))]

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubBufferedProducerClient", typeof(AzureMessagingEventHubsBufferedProducerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubBufferedProducerClient:ClientOptions", typeof(EventHubBufferedProducerClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubBufferedProducerClient:*", typeof(AzureMessagingEventHubsBufferedProducerSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:EventHubs:EventHubBufferedProducerClient:*:ClientOptions", typeof(EventHubBufferedProducerClientOptions))]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity",
    "Azure.Messaging.EventHubs")]

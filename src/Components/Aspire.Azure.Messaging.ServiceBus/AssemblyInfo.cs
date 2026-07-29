// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus;

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:ServiceBus", typeof(AzureMessagingServiceBusSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:ServiceBus:ClientOptions", typeof(ServiceBusClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:ServiceBus:*", typeof(AzureMessagingServiceBusSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:ServiceBus:*:ClientOptions", typeof(ServiceBusClientOptions))]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity",
    "Azure.Messaging.ServiceBus")]

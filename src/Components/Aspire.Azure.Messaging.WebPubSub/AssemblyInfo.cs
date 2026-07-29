// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Messaging.WebPubSub;
using Azure.Messaging.WebPubSub;

[assembly: ConfigurationSchema("Aspire:Azure:Messaging:WebPubSub", typeof(AzureMessagingWebPubSubSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:WebPubSub:ClientOptions", typeof(WebPubSubServiceClientOptions), exclusionPaths: ["Default"])]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:WebPubSub:*", typeof(AzureMessagingWebPubSubSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:WebPubSub:*:ClientOptions", typeof(WebPubSubServiceClientOptions), exclusionPaths: ["Default"])]
[assembly: ConfigurationSchema("Aspire:Azure:Messaging:WebPubSub:*:*", typeof(AzureMessagingWebPubSubSettings))]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity",
    "Azure.Messaging.WebPubSub")]

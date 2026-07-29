// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.AI.OpenAI;
using Aspire;
using Azure.AI.OpenAI;

[assembly: ConfigurationSchema("Aspire:Azure:AI:OpenAI", typeof(AzureOpenAISettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:OpenAI:ClientOptions", typeof(AzureOpenAIClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:OpenAI:*", typeof(AzureOpenAISettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:OpenAI:*:ClientOptions", typeof(AzureOpenAIClientOptions))]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity"
)]

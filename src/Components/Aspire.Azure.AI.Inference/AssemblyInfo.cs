// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Inference;
using Azure.AI.Inference;

[assembly: ConfigurationSchema("Aspire:Azure:AI:Inference", typeof(ChatCompletionsClientSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:Inference:ClientOptions", typeof(AzureAIInferenceClientOptions))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:Inference:*", typeof(ChatCompletionsClientSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:Inference:*:ClientOptions", typeof(AzureAIInferenceClientOptions))]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity"
)]

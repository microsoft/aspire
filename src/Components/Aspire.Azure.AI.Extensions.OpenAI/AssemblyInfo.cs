// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Extensions.OpenAI;
using Azure.AI.Extensions.OpenAI;

[assembly: ConfigurationSchema("Aspire:Azure:AI:Extensions:OpenAI", typeof(AzureProjectOpenAISettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:Extensions:OpenAI:ClientOptions", typeof(ProjectOpenAIClientOptions))]
[assembly: LoggingCategories("Azure", "Azure.Core", "Azure.Identity")]

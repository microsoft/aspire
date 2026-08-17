// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.OpenAI;
using Aspire;
using OpenAI;

[assembly: ConfigurationSchema("Aspire:OpenAI", typeof(OpenAISettings))]
[assembly: ConfigurationSchema("Aspire:OpenAI:ClientOptions", typeof(OpenAIClientOptions))]
[assembly: ConfigurationSchema("Aspire:OpenAI:*", typeof(OpenAISettings))]
[assembly: ConfigurationSchema("Aspire:OpenAI:*:ClientOptions", typeof(OpenAIClientOptions))]

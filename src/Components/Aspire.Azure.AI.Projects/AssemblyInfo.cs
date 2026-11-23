// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Projects;
using Azure.AI.Projects;

[assembly: ConfigurationSchema("Aspire:Azure:AI:Projects", typeof(AzureAIProjectsSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:AI:Projects:ClientOptions", typeof(AIProjectClientOptions))]
[assembly: LoggingCategories("Azure", "Azure.Core", "Azure.Identity")]

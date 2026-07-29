// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.Azure.Cosmos;

[assembly: ConfigurationSchema("Aspire:Microsoft:Azure:Cosmos", typeof(MicrosoftAzureCosmosSettings))]
[assembly: ConfigurationSchema("Aspire:Microsoft:Azure:Cosmos:*", typeof(MicrosoftAzureCosmosSettings))]

[assembly: LoggingCategories("Azure-Cosmos-Operation-Request-Diagnostics")]

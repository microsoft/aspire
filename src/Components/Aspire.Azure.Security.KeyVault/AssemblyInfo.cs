// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Security.KeyVault;
using Azure.Security.KeyVault.Secrets;

[assembly: ConfigurationSchema("Aspire:Azure:Security:KeyVault", typeof(AzureSecurityKeyVaultSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Security:KeyVault:ClientOptions", typeof(SecretClientOptions), exclusionPaths: ["Default"])]
[assembly: ConfigurationSchema("Aspire:Azure:Security:KeyVault:*", typeof(AzureSecurityKeyVaultSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Security:KeyVault:*:ClientOptions", typeof(SecretClientOptions), exclusionPaths: ["Default"])]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity")]

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Data.Tables;
using Azure.Data.Tables;

[assembly: ConfigurationSchema("Aspire:Azure:Data:Tables", typeof(AzureDataTablesSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Data:Tables:ClientOptions", typeof(TableClientOptions), exclusionPaths: ["Default"])]
[assembly: ConfigurationSchema("Aspire:Azure:Data:Tables:*", typeof(AzureDataTablesSettings))]
[assembly: ConfigurationSchema("Aspire:Azure:Data:Tables:*:ClientOptions", typeof(TableClientOptions), exclusionPaths: ["Default"])]

[assembly: LoggingCategories(
    "Azure",
    "Azure.Core",
    "Azure.Identity")]

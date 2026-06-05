// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake;

[assembly: ConfigurationSchema("Aspire:Azure:Storage:Files:DataLake", typeof(AzureDataLakeSettings))]
[assembly:
    ConfigurationSchema(
        "Aspire:Azure:Storage:Files:DataLake:ClientOptions",
        typeof(DataLakeClientOptions),
        exclusionPaths: ["Default"])]

[assembly: LoggingCategories("Azure", "Azure.Core", "Azure.Identity")]

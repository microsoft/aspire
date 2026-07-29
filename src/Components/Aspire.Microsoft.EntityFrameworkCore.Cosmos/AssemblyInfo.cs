// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.EntityFrameworkCore.Cosmos;

[assembly: ConfigurationSchema("Aspire:Microsoft:EntityFrameworkCore:Cosmos", typeof(EntityFrameworkCoreCosmosSettings))]
[assembly: ConfigurationSchema("Aspire:Microsoft:EntityFrameworkCore:Cosmos:*", typeof(EntityFrameworkCoreCosmosSettings))]

[assembly: LoggingCategories(
    "Azure-Cosmos-Operation-Request-Diagnostics",
    "Microsoft.EntityFrameworkCore",
    "Microsoft.EntityFrameworkCore.ChangeTracking",
    "Microsoft.EntityFrameworkCore.Database",
    "Microsoft.EntityFrameworkCore.Database.Command",
    "Microsoft.EntityFrameworkCore.Infrastructure",
    "Microsoft.EntityFrameworkCore.Query")]

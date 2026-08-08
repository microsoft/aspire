// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.EntityFrameworkCore.SqlServer;

[assembly: ConfigurationSchema("Aspire:Microsoft:EntityFrameworkCore:SqlServer", typeof(MicrosoftEntityFrameworkCoreSqlServerSettings))]
[assembly: ConfigurationSchema("Aspire:Microsoft:EntityFrameworkCore:SqlServer:*", typeof(MicrosoftEntityFrameworkCoreSqlServerSettings))]

[assembly: LoggingCategories(
    "Microsoft.EntityFrameworkCore",
    "Microsoft.EntityFrameworkCore.ChangeTracking",
    "Microsoft.EntityFrameworkCore.Database",
    "Microsoft.EntityFrameworkCore.Database.Command",
    "Microsoft.EntityFrameworkCore.Database.Connection",
    "Microsoft.EntityFrameworkCore.Database.Transaction",
    "Microsoft.EntityFrameworkCore.Infrastructure",
    "Microsoft.EntityFrameworkCore.Migrations",
    "Microsoft.EntityFrameworkCore.Model",
    "Microsoft.EntityFrameworkCore.Model.Validation",
    "Microsoft.EntityFrameworkCore.Query",
    "Microsoft.EntityFrameworkCore.Update")]

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Npgsql;
using Aspire;

[assembly: ConfigurationSchema("Aspire:Npgsql", typeof(NpgsqlSettings))]
[assembly: ConfigurationSchema("Aspire:Npgsql:*", typeof(NpgsqlSettings))]

[assembly: LoggingCategories(
    "Npgsql",
    "Npgsql.Command",
    "Npgsql.Connection",
    "Npgsql.Copy",
    "Npgsql.Exception",
    "Npgsql.Replication",
    "Npgsql.Transaction")]

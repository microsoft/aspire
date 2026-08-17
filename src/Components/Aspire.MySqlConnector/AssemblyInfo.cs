// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.MySqlConnector;
using Aspire;

[assembly: ConfigurationSchema("Aspire:MySqlConnector", typeof(MySqlConnectorSettings))]
[assembly: ConfigurationSchema("Aspire:MySqlConnector:*", typeof(MySqlConnectorSettings))]

[assembly: LoggingCategories(
    "MySqlConnector",
    "MySqlConnector.ConnectionPool",
    "MySqlConnector.MySqlBulkCopy",
    "MySqlConnector.MySqlCommand",
    "MySqlConnector.MySqlConnection",
    "MySqlConnector.MySqlDataSource")]

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.Data.SqlClient;

[assembly: ConfigurationSchema("Aspire:Microsoft:Data:SqlClient", typeof(MicrosoftDataSqlClientSettings))]
[assembly: ConfigurationSchema("Aspire:Microsoft:Data:SqlClient:*", typeof(MicrosoftDataSqlClientSettings))]

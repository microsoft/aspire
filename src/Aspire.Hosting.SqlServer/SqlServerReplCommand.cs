// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Provides well-known sqlcmd executable paths for the SQL Server REPL.
/// </summary>
/// <remarks>
/// Use these values with <see cref="SqlServerReplOptions.Command"/> to select the client
/// installed in the SQL Server container, or specify a custom executable path.
/// </remarks>
public static class SqlServerReplCommand
{
    /// <summary>
    /// The sqlcmd executable path for SQL Server client tools version 17.
    /// </summary>
    [AspireValue("SqlServerReplCommand")]
    public const string Version17 = "/opt/mssql-tools/bin/sqlcmd";

    /// <summary>
    /// The sqlcmd executable path for SQL Server client tools version 18.
    /// </summary>
    [AspireValue("SqlServerReplCommand")]
    public const string Version18 = "/opt/mssql-tools18/bin/sqlcmd";
}

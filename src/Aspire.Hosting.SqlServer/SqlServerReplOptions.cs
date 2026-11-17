// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Options for configuring the SQL Server REPL.
/// </summary>
/// <remarks>
/// The selected sqlcmd client must already be installed in the SQL Server container.
/// These options do not install a client or change the container image.
/// </remarks>
/// <example>
/// Select the legacy sqlcmd client when using an older SQL Server image:
/// <code>
/// builder.AddSqlServer("sql")
///     .WithRepl(options => options.Command = SqlServerReplCommand.Version17);
/// </code>
/// </example>
[AspireExport]
public sealed class SqlServerReplOptions
{
    /// <summary>
    /// Gets or sets the sqlcmd executable path inside the SQL Server container.
    /// Defaults to <see cref="SqlServerReplCommand.Version18"/>, matching the integration's default SQL Server image.
    /// </summary>
    /// <remarks>
    /// Use a well-known path from <see cref="SqlServerReplCommand"/> or a custom executable path.
    /// The executable is invoked directly, without shell interpretation, using the sqlcmd arguments.
    /// </remarks>
    [AspireExport]
    public string Command { get; set; } = SqlServerContainerImageTags.ReplCommand;
}

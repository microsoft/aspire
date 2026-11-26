// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;

namespace Aspire.Cli.Utils;

/// <summary>
/// Extension methods for Spectre.Console <see cref="Table"/>.
/// </summary>
internal static class TableExtensions
{
    /// <summary>
    /// Creates a table shaped for the current standard output.
    /// </summary>
    /// <remarks>
    /// When stdout is not an interactive terminal (redirected to a file, a pipe, or an agent's
    /// tool call), box-drawing borders add several multi-byte characters per cell without
    /// conveying any data, so the table is rendered without borders and keeps only the
    /// space-aligned columns.
    /// </remarks>
    public static Table CreateTable(this ICliHostEnvironment hostEnvironment)
    {
        var table = new Table();

        if (!hostEnvironment.SupportsInteractiveOutput)
        {
            table.Border(TableBorder.None);
        }

        return table;
    }

    /// <summary>
    /// Adds a column with a bold header to the table.
    /// </summary>
    public static Table AddBoldColumn(this Table table, string header, bool noWrap = false, int? width = null)
    {
        var column = new TableColumn($"[bold]{header.EscapeMarkup()}[/]");

        if (noWrap)
        {
            column.NoWrap();
        }

        if (width is not null)
        {
            column.Width = width;
        }

        table.AddColumn(column);
        return table;
    }
}

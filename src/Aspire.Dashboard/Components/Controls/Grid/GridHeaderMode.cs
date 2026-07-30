// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Controls.Grid;

/// <summary>
/// Controls how the header row of a Deck data table is rendered.
/// </summary>
public enum GridHeaderMode
{
    /// <summary>
    /// Render a standard header row that scrolls with the table.
    /// </summary>
    Default,

    /// <summary>
    /// Render a header row that sticks to the top of the scroll container.
    /// </summary>
    Sticky,

    /// <summary>
    /// Do not render a header row.
    /// </summary>
    None
}

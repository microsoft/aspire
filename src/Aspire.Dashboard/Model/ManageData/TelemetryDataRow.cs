// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Model.ManageData;

/// <summary>
/// Represents a nested telemetry data row within a resource row.
/// </summary>
public sealed class TelemetryDataRow
{
    /// <summary>
    /// The type of data this row represents.
    /// </summary>
    public required AspireDataType DataType { get; init; }

    /// <summary>
    /// The icon representing this data type.
    /// </summary>
    public required DeckIconName Icon { get; init; }

    /// <summary>
    /// The URL to navigate to when clicking on this data row.
    /// </summary>
    public required string Url { get; init; }
}

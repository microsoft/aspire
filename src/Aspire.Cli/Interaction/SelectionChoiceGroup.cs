// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;

namespace Aspire.Cli.Interaction;

internal sealed record SelectionChoiceGroup(string Id, string Label)
{
    public Style? Style { get; init; }
}

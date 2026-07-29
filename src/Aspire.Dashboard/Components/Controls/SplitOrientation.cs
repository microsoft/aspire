// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Controls;

public enum SplitOrientation
{
    Horizontal,
    Vertical
}

public sealed record SplitResizedEventArgs(double Panel1Size, double Panel2Size);

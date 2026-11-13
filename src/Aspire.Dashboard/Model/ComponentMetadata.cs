// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Model;

public sealed class ComponentMetadata
{
    // Match DynamicComponent.Type so runtime activation and parameter binding survive trimming.
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public required Type Type { get; init; }
    public Dictionary<string, object> Parameters { get; } = [];
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Shared;

namespace Aspire.Dashboard.Extensions;

internal static class AssemblyExtensions
{
    public static string? GetDisplayVersion(this Assembly assembly)
    {
        return AssemblyVersionHelper.GetDisplayVersion(assembly);
    }
}

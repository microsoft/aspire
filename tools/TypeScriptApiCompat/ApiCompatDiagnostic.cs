// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal sealed record ApiCompatDiagnostic(string Kind, string PackageName, string Symbol, string Message)
{
    public string SuppressionKey => $"{Kind}|{PackageName}|{Symbol}";
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal sealed record CommandLineOptions(
    string BaselinePath,
    string CurrentPath,
    string SuppressionsRoot,
    string? BaselineSuppressionsRoot,
    string? ReportPath,
    bool GitHubAnnotations);

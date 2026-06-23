// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides the path to the running Aspire CLI process.
/// </summary>
internal interface IProcessPathProvider
{
    string? ProcessPath { get; }
}

internal sealed class EnvironmentProcessPathProvider : IProcessPathProvider
{
    public string? ProcessPath => DotNetToolDetection.GetCurrentProcessPath();
}

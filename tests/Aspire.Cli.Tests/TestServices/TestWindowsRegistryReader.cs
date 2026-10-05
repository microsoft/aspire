// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestWindowsRegistryReader(bool hasWingetAspireUninstallEntry = false) : IWindowsRegistryReader
{
    public Func<string, bool>? ProbeCallback { get; init; }

    public bool HasWingetAspireUninstallEntry(string processPath) => ProbeCallback?.Invoke(processPath) ?? hasWingetAspireUninstallEntry;
}
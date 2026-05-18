// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal sealed class BrowserProcessResult(int exitCode)
{
    public int ExitCode { get; } = exitCode;
}

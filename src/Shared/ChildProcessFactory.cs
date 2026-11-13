// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Creates configured child executions without starting them.
/// </summary>
internal interface IChildProcessFactory
{
    IChildProcess Create(ProcessStartInfo startInfo, ILogger logger, ChildProcessOptions options, bool isWindows);
}

/// <summary>
/// Creates the shared process implementation.
/// </summary>
internal sealed class ChildProcessFactory : IChildProcessFactory
{
    public IChildProcess Create(ProcessStartInfo startInfo, ILogger logger, ChildProcessOptions options, bool isWindows)
        => new ChildProcess(startInfo, logger, options, isWindows);
}

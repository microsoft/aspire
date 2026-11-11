// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

internal sealed class TemplateDiscoveryException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

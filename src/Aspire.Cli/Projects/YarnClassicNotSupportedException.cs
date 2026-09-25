// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal sealed class YarnClassicNotSupportedException(string message) : InvalidOperationException(message)
{
}

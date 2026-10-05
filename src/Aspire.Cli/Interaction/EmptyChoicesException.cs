// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

internal sealed class EmptyChoicesException(string message) : Exception(message)
{
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestStartupErrorWriter : IStartupErrorWriter
{
    public List<string> Lines { get; } = [];
    public List<string> MarkupLines { get; } = [];

    public void WriteLine(string message, KnownEmoji? emoji = null) => Lines.Add(message);

    public void WriteMarkup(string markup, KnownEmoji? emoji = null) => MarkupLines.Add(markup);

    public void Dispose()
    {
        // No-op in tests — don't write log file path to output
    }
}

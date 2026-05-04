// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDetachedProcessLauncher : IDetachedProcessLauncher
{
    public TestDetachedProcess Process { get; } = new();

    public IReadOnlyList<string> Arguments { get; private set; } = [];

    public IDetachedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
    {
        Arguments = arguments;
        return Process;
    }
}

internal sealed class TestDetachedProcess : IDetachedProcess
{
    public int Id => 1;

    public bool HasExited => false;

    public int ExitCode => 0;

    public bool Killed { get; private set; }

    public bool KilledEntireProcessTree { get; private set; }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
    }

    public void Kill(bool entireProcessTree)
    {
        Killed = true;
        KilledEntireProcessTree = entireProcessTree;
    }
}

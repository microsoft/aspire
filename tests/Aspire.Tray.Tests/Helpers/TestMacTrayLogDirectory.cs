// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestMacTrayLogDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-log-");

    public string Root { get; }
    public string LogPath => Path.Combine(Root, "logs", "tray.log");

    public TestMacTrayLogDirectory()
    {
        // macOS's temporary directory uses /var -> /private/var. Resolve this trusted
        // fixture path so the production no-follow walk can reject every symlink.
        Root = "/";
        foreach (var component in _directory.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(Root, component));
            Root = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
    }

    public void Dispose() => _directory.Delete(recursive: true);
}

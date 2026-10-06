// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestTrayStateDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-state-");

    public string StatePath => Path.Combine(_directory.FullName, "state", "history.json");
    public string UserProfilePath => Path.Combine(_directory.FullName, "user");
    public string LegacyDirectory => Path.Combine(_directory.FullName, "legacy");
    public string UserStatePath => Path.Combine(UserProfilePath, ".aspire", "tray", "apphosts.json");

    public string CreateAppHost(string relativePath)
    {
        // Fixtures use paths like "project/apphost.cs"; normalize separators before
        // storing or comparing them with paths returned by the production code.
        var path = Path.GetFullPath(Path.Combine(_directory.FullName, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    public void WriteState(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, contents);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}

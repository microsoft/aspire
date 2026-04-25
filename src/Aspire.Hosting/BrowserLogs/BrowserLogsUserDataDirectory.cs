// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

namespace Aspire.Hosting;

internal sealed class BrowserLogsUserDataDirectory : IDisposable
{
    private readonly TempDirectory? _temporaryDirectory;

    private BrowserLogsUserDataDirectory(string path, string? profileDirectoryName, TempDirectory? temporaryDirectory)
    {
        Path = path;
        ProfileDirectoryName = profileDirectoryName;
        _temporaryDirectory = temporaryDirectory;
    }

    public string Path { get; }

    public string? ProfileDirectoryName { get; }

    public bool IsTemporary => _temporaryDirectory is not null;

    public static BrowserLogsUserDataDirectory CreatePersistent(string path, string? profileDirectoryName) =>
        new(path, profileDirectoryName, temporaryDirectory: null);

    public static BrowserLogsUserDataDirectory CreateTemporary(TempDirectory temporaryDirectory) =>
        new(temporaryDirectory.Path, profileDirectoryName: null, temporaryDirectory);

    public void Dispose() => _temporaryDirectory?.Dispose();
}

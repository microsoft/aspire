// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Shared;

namespace Aspire.Tray;

/// <summary>
/// Atomically persists history in a private, explicitly selected per-user state directory.
/// </summary>
internal sealed class FileTraySavedStateStore : ITraySavedStateStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string _path;
    private readonly string _directory;
    private bool _loaded;
    private bool _writable;
    private byte[]? _lastContents;

    public FileTraySavedStateStore(string path)
    {
        _path = TrayAppHostPath.Normalize(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("A saved-state file path is required.", nameof(path));
    }

    public TraySavedState Load()
    {
        _loaded = true;
        _writable = false;
        var contents = ReadContents();
        // Only paths and flags are accepted:
        // {"appHosts":[{"appHostPath":"/src/shop/apphost.cs","isPinned":true,"isRecent":true}]}
        // Array order is recency; no timestamp, PID, or dashboard URL is persisted.
        var state = contents is null ? TraySavedState.Empty
            : JsonSerializer.Deserialize(contents, TraySavedStateJsonContext.Default.TraySavedState)
                ?? throw new InvalidDataException("The saved AppHost state is invalid.");
        Validate(state);
        _lastContents = contents;
        _writable = true;
        return state;
    }

    public void Save(TraySavedState state)
    {
        if (!_loaded)
        {
            Load();
        }
        if (!_writable)
        {
            throw new InvalidDataException("Saved AppHost state could not be loaded; the file was left unchanged.");
        }
        Validate(state);
        var current = ReadContents();
        if (!SameContents(current, _lastContents))
        {
            _writable = false;
            throw new IOException("Saved AppHost state changed outside the tray; the file was left unchanged.");
        }
        var contents = JsonSerializer.SerializeToUtf8Bytes(state, TraySavedStateJsonContext.Default.TraySavedState);
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Saved AppHost state exceeds the size limit.");
        }

        DirectoryHelper.CreateWithOwnerOnlyPermissions(_directory);
        // A private same-directory temporary file keeps replacement on the same filesystem.
        // CreateNew avoids following a pre-existing link; rename never truncates the original.
        var temporaryPath = Path.Combine(_directory, $".history-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            _lastContents = contents;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private byte[]? ReadContents()
    {
        RejectLink(new DirectoryInfo(_directory));
        RejectLink(new FileInfo(_path));
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Saved AppHost state exceeds the size limit.");
            }
            var contents = new byte[(int)stream.Length];
            stream.ReadExactly(contents);
            return contents;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Saved AppHost state must not use symbolic links.");
        }
    }

    private static bool SameContents(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static void Validate(TraySavedState state)
    {
        if (state.AppHosts is null)
        {
            throw new InvalidDataException("The saved AppHost state is invalid.");
        }
        var paths = new HashSet<string>(TrayAppHostPath.Comparer);
        var recent = 0;
        foreach (var host in state.AppHosts)
        {
            if (host is null || (!host.IsPinned && !host.IsRecent)
                || string.IsNullOrWhiteSpace(host.AppHostPath)
                || !Path.IsPathFullyQualified(host.AppHostPath) || host.AppHostPath.Contains('\0'))
            {
                throw new InvalidDataException("The saved AppHost state is invalid.");
            }
            var normalized = TrayAppHostPath.Normalize(host.AppHostPath);
            if (!string.Equals(normalized, host.AppHostPath, StringComparison.Ordinal) || !paths.Add(normalized)
                || (host.IsRecent && ++recent > TraySavedState.MaximumRecentAppHosts))
            {
                throw new InvalidDataException("The saved AppHost state is invalid.");
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TraySavedState))]
internal sealed partial class TraySavedStateJsonContext : JsonSerializerContext
{
}

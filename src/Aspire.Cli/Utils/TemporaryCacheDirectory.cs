// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Owns a leased temporary directory under a CLI-managed cache root.
/// </summary>
internal sealed class TemporaryCacheDirectory : IDisposable
{
    private readonly string _leasePath;
    private readonly FileStream _lease;
    private readonly Action<string> _deleteDirectory;
    private readonly Action<string> _deleteFile;
    private bool _deleteOnDispose = true;
    private bool _disposed;

    private TemporaryCacheDirectory(
        string fullName,
        string leasePath,
        FileStream lease,
        Action<string> deleteDirectory,
        Action<string> deleteFile)
    {
        FullName = fullName;
        _leasePath = leasePath;
        _lease = lease;
        _deleteDirectory = deleteDirectory;
        _deleteFile = deleteFile;
    }

    public string FullName { get; }

    public static TemporaryCacheDirectory Create(
        string parentDirectory,
        string prefix,
        Action<string> deleteDirectory,
        Action<string> deleteFile)
    {
        var fullName = Path.Combine(parentDirectory, $".{prefix}-{Guid.NewGuid():N}");
        var leasePath = GetLeasePath(fullName);
        FileStream? lease = null;

        try
        {
            // Acquire the sibling lease before making the directory visible. A concurrent stale-directory
            // sweep can therefore never claim and remove a directory between its creation and lease acquisition.
            lease = OpenLease(fullName);
            Directory.CreateDirectory(fullName);
            return new TemporaryCacheDirectory(
                fullName,
                leasePath,
                lease,
                deleteDirectory,
                deleteFile);
        }
        catch
        {
            lease?.Dispose();
            deleteDirectory(fullName);
            deleteFile(leasePath);
            throw;
        }
    }

    public void MoveTo(string targetDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.Move(FullName, targetDirectory);
        _deleteOnDispose = false;
    }

    public static FileStream OpenLease(string directory)
    {
        return new FileStream(
            GetLeasePath(directory),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
    }

    public static string GetLeasePath(string directory)
    {
        return $"{directory}.lock";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_deleteOnDispose)
        {
            _deleteDirectory(FullName);
        }

        _lease.Dispose();
        _deleteFile(_leasePath);
    }
}

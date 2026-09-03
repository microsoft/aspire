// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Owns a leased temporary directory under the Aspire skills cache root.
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
        Directory.CreateDirectory(fullName);
        var leasePath = GetLeasePath(fullName);

        try
        {
            return new TemporaryCacheDirectory(
                fullName,
                leasePath,
                OpenLease(fullName),
                deleteDirectory,
                deleteFile);
        }
        catch
        {
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

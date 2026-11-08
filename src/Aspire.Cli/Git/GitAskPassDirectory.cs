// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Backchannel;
using Aspire.Shared;

namespace Aspire.Cli.Git;

internal sealed class GitAskPassDirectory : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly DirectoryInfo _directory;
    private bool _helperCreated;

    private GitAskPassDirectory(DirectoryInfo directory, string socketPath)
    {
        _directory = directory;
        SocketPath = socketPath;
    }

    public string SocketPath { get; }

    internal static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "git");

    public static GitAskPassDirectory Create()
    {
        var path = Path.Combine(RootPath, BackchannelConstants.CreateRandomIdentifier(32));
        var socketPath = Path.Combine(path, BackchannelConstants.ComputeSocketFileName("git.askpass"));
        BackchannelConstants.ValidateSocketPathLength(socketPath);
        // Do not repair or adopt public directories. The shared helper rejects links
        // and verifies 0700 on Unix or a protected current-user DACL on Windows.
        SocketPermissionHelper.CreateDirectory(RootPath, repairExisting: false);
        if (Path.Exists(path))
        {
            throw new IOException("The Git credential callback operation directory already exists.");
        }
        return new GitAskPassDirectory(SocketPermissionHelper.CreateDirectory(path, repairExisting: false), socketPath);
    }

    public void Bind(Socket socket)
    {
        // The directory is private throughout bind/chmod. Git is not started until
        // the shared binder has restricted the socket and validation has succeeded.
        SocketPermissionHelper.Bind(socket, SocketPath);
        ValidateEndpoint(SocketPath);
    }

    public string CreateHelper(string command)
    {
        var path = Path.Combine(_directory.FullName, "askpass");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }
        // Git executes an executable path, not a command string. Git for Windows
        // also interprets the shebang through its bundled shell.
        // https://git-scm.com/docs/gitcredentials#_requesting_credentials
        using (var stream = new FileStream(path, options))
        {
            _helperCreated = true;
            stream.Write(System.Text.Encoding.UTF8.GetBytes($"#!/bin/sh\nexec {command} \"$@\"\n"));
        }
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != OwnerOnly)
        {
            throw new IOException("The Git credential callback launcher must have mode 0700.");
        }
        return path;
    }

    public static void ValidateEndpoint(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directory = Path.GetDirectoryName(path);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, comparison) ||
            !string.Equals(Path.GetDirectoryName(directory), RootPath, comparison) ||
            directory is null || !Directory.Exists(directory) || !File.Exists(path))
        {
            throw new IOException("Invalid Git credential callback endpoint.");
        }
        var operation = Path.GetFileName(directory);
        if (operation.Length != 32 || !operation.All(char.IsAsciiHexDigit))
        {
            throw new IOException("Invalid Git credential callback operation.");
        }
        BackchannelConstants.ValidateSocketPathLength(path);
        SocketPermissionHelper.CreateDirectory(directory, repairExisting: false);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new IOException("The Git credential callback socket must have mode 0600.");
        }
    }

    public void Dispose()
    {
        // Socket.Dispose normally unlinks its endpoint. Only these exact operation
        // paths are ours; never recursively delete a directory or sweep old sessions.
        File.Delete(SocketPath);
        if (_helperCreated)
        {
            File.Delete(Path.Combine(_directory.FullName, "askpass"));
        }
        _directory.Delete();
    }
}

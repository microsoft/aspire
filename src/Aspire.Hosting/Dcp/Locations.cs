// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Dcp;

internal sealed class Locations
{
    private readonly IFileSystemService _directoryService;
    private TempDirectory? _dcpSessionDir;

    public Locations(IFileSystemService directoryService)
    {
        _directoryService = directoryService;
    }

    public string DcpSessionDir => GetOrCreateDcpSessionDir();

    public string DcpKubeconfigPath => Path.Combine(DcpSessionDir, "kubeconfig");

    public string DcpLogSocket => Path.Combine(DcpSessionDir, "output.sock");

    /// <summary>
    /// Hands ownership of the session directory to DCP, which was started with it as <c>DCP_SESSION_FOLDER</c>.
    /// </summary>
    /// <remarks>
    /// DCP runs detached, still reads files such as the kubeconfig from the session directory during its own
    /// cleanup after the AppHost has shut down, and deletes the directory when it is done. The managed side must
    /// therefore stop tracking the directory so that disposing the file system service does not delete it first.
    /// </remarks>
    public void TransferDcpSessionDirOwnershipToDcp()
    {
        if (_dcpSessionDir is not null && _directoryService is FileSystemService fileSystemService)
        {
            fileSystemService.UntrackItem(_dcpSessionDir.Path);
        }
    }

    /// <summary>
    /// Deletes the session directory when DCP failed to start and therefore will not delete it.
    /// </summary>
    public void DeleteDcpSessionDir()
    {
        _dcpSessionDir?.Dispose();
    }

    private string GetOrCreateDcpSessionDir()
    {
        if (_dcpSessionDir == null)
        {
            // Use the temp directory service to create a DCP-specific subdirectory
            _dcpSessionDir = _directoryService.TempDirectory.CreateTempSubdirectory(SocketDirectoryNames.DcpPrefix);
        }

        return _dcpSessionDir.Path;
    }
}

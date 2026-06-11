// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

namespace Aspire.Hosting.Dcp;

internal sealed class Locations
{
    private readonly IFileSystemService _directoryService;
    private string? _dcpSessionDir;

    public Locations(IFileSystemService directoryService)
    {
        _directoryService = directoryService;
    }

    public string DcpSessionDir => GetOrCreateDcpSessionDir();

    public string DcpKubeconfigPath => Path.Combine(DcpSessionDir, "kubeconfig");

    public string DcpLogSocket => Path.Combine(DcpSessionDir, "output.sock");

    private string GetOrCreateDcpSessionDir()
    {
        if (_dcpSessionDir == null)
        {
            // Use the temp directory service to create a DCP-specific subdirectory
            _dcpSessionDir = _directoryService.TempDirectory.CreateTempSubdirectory("aspire-dcp").Path;
        }

        return _dcpSessionDir;
    }
}

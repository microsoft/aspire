// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

/// <summary>Owns disposable history, archive, extraction and request workspaces separate from retained outputs.</summary>
internal sealed class AnalysisScratchWorkspace : IDisposable
{
    private readonly DirectoryInfo _directory;

    internal AnalysisScratchWorkspace()
    {
        _directory = Directory.CreateTempSubdirectory("aspire-ci-analysis-");
    }

    internal AnalysisScratchWorkspace(string directory)
    {
        _directory = Directory.CreateDirectory(directory);
    }

    internal string RootDirectory => _directory.FullName;
    internal string HistoryCheckout => Path.Combine(RootDirectory, "history-checkout");
    internal string Archives => Path.Combine(RootDirectory, "archives");
    internal string Results => Path.Combine(RootDirectory, "extracted-results");
    internal string Requests => Path.Combine(RootDirectory, "requests");

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide the collection/publication failure or prevent artifact upload.
            Console.Error.WriteLine($"::warning::Unable to remove analysis scratch: {CiFailureJson.Display(error.Message)}");
        }
    }
}

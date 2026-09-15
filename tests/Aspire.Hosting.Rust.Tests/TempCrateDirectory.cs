// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Rust.Tests;

/// <summary>
/// Creates a securely-created temporary directory that stands in for a Rust crate directory.
/// </summary>
internal sealed class TempCrateDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-rust-tests");

    public string Path => _directory.FullName;

    public void Write(string fileName, string content)
        => File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a virus scanner or indexer can briefly hold a handle on Windows, and failing
            // to clean up a temp directory must not fail an otherwise passing test.
        }
    }
}

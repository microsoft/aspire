// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

namespace Aspire.Hosting.Utils;

/// <summary>
/// A simple <see cref="IFileSystemService"/> implementation for tests that delegates to real temp directory operations.
/// Dispose to clean up all created temp directories.
/// </summary>
internal sealed class TestFileSystemService : IFileSystemService, IDisposable
{
    private readonly TestTempFileSystemService _tempDirectory = new();

    public ITempFileSystemService TempDirectory => _tempDirectory;

    public void Dispose() => _tempDirectory.Dispose();

    private sealed class TestTempFileSystemService : ITempFileSystemService, IDisposable
    {
        private readonly List<string> _directories = [];

        public TempDirectory CreateTempSubdirectory(string? prefix = null)
        {
            var dir = Directory.CreateTempSubdirectory(prefix);
            _directories.Add(dir.FullName);
            return new TestTempDirectory(dir.FullName);
        }

        public TempFile CreateTempFile(string? fileName = null)
        {
            var tempDir = Directory.CreateTempSubdirectory("aspire");
            _directories.Add(tempDir.FullName);
            var resolvedName = fileName ?? System.IO.Path.GetRandomFileName();
            var filePath = System.IO.Path.Combine(tempDir.FullName, resolvedName);
            File.Create(filePath).Dispose();
            return new TestTempFile(filePath, tempDir.FullName);
        }

        public void Dispose()
        {
            foreach (var dir in _directories)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private sealed class TestTempDirectory(string path) : TempDirectory
    {
        public override string Path => path;

        public override void Dispose()
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class TestTempFile(string path, string parentDir) : TempFile
    {
        public override string Path => path;

        public override void Dispose()
        {
            try
            {
                if (Directory.Exists(parentDir))
                {
                    Directory.Delete(parentDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}

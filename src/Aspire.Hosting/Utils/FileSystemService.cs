// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Default implementation of <see cref="IFileSystemService"/>.
/// </summary>
internal sealed class FileSystemService : IFileSystemService, IDisposable
{
    private readonly TempFileSystemService _tempDirectory;
    private ILogger? _logger;
    private readonly bool _preserveTempFiles;

    // Track allocated temp files and directories as disposable objects using path as key
    private readonly ConcurrentDictionary<string, IDisposable> _allocatedItems = new();

    public FileSystemService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Check configuration to preserve temp files for debugging
        _preserveTempFiles = configuration["ASPIRE_PRESERVE_TEMP_FILES"] is not null;
        
        _tempDirectory = new TempFileSystemService(this);
    }

    /// <summary>
    /// Sets the logger for this service. Called after service provider is built.
    /// </summary>
    /// <remarks>
    /// The logger cannot be injected via constructor because the FileSystemService
    /// is allocated before logging is fully initialized in the DistributedApplicationBuilder.
    /// </remarks>
    internal void SetLogger(ILogger<FileSystemService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public ITempFileSystemService TempDirectory => _tempDirectory;

    /// <summary>
    /// Gets whether temporary files should be preserved for debugging.
    /// </summary>
    internal bool ShouldPreserveTempFiles => _preserveTempFiles;

    /// <summary>
    /// Gets the logger for this service, if set.
    /// </summary>
    internal ILogger? Logger => _logger;

    private bool _disposed;
    private readonly object _disposeLock = new();

    /// <summary>
    /// Tracks a temporary item for cleanup on service disposal.
    /// </summary>
    internal void TrackItem(string path, IDisposable item)
    {
        lock (_disposeLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileSystemService), "Cannot allocate temporary files after the service has been disposed.");
            }

            _allocatedItems.TryAdd(path, item);
        }
    }

    /// <summary>
    /// Removes a temporary item from tracking.
    /// </summary>
    internal void UntrackItem(string path)
    {
        _allocatedItems.TryRemove(path, out _);
    }

    /// <summary>
    /// Cleans up any remaining temporary files and directories.
    /// </summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_preserveTempFiles)
        {
            _logger?.LogInformation("Skipping cleanup of {Count} temporary files/directories due to ASPIRE_PRESERVE_TEMP_FILES configuration", _allocatedItems.Count);
            return;
        }

        if (_allocatedItems.IsEmpty)
        {
            return;
        }

        _logger?.LogDebug("Cleaning up {Count} remaining temporary files/directories", _allocatedItems.Count);

        foreach (var kvp in _allocatedItems)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clean up temporary item");
            }
        }
    }

    /// <summary>
    /// Implementation of <see cref="ITempFileSystemService"/>.
    /// </summary>
    private sealed class TempFileSystemService : ITempFileSystemService
    {
        private readonly FileSystemService _parent;

        public TempFileSystemService(FileSystemService parent)
        {
            _parent = parent;
        }

        /// <inheritdoc/>
        public TempDirectory CreateTempSubdirectory(string? prefix = null)
        {
            var path = Directory.CreateTempSubdirectory(prefix ?? "aspire").FullName;
            var tempDir = new DefaultTempDirectory(path, _parent);
            _parent.TrackItem(path, tempDir);
            return tempDir;
        }

        /// <inheritdoc/>
        public TempFile CreateTempFile(string? fileName = null)
        {
            if (fileName is null)
            {
                var tempFile = Path.GetTempFileName();
                var file = new DefaultTempFile(tempFile, deleteParentDirectory: false, _parent);
                _parent.TrackItem(tempFile, file);
                return file;
            }

            // Create a temp subdirectory and place the named file inside it
            var tempDir = Directory.CreateTempSubdirectory("aspire").FullName;
            var filePath = Path.Combine(tempDir, fileName);
            File.Create(filePath).Dispose();
            var tempFileObj = new DefaultTempFile(filePath, deleteParentDirectory: true, _parent);
            _parent.TrackItem(filePath, tempFileObj);
            return tempFileObj;
        }
    }

    /// <summary>
    /// Default implementation of <see cref="TempDirectory"/>.
    /// </summary>
    private sealed class DefaultTempDirectory : TempDirectory
    {
        private readonly string _path;
        private readonly FileSystemService _parent;
        private bool _disposed;

        public DefaultTempDirectory(string path, FileSystemService parent)
        {
            _path = path;
            _parent = parent;

            _parent.Logger?.LogDebug("Allocated temporary directory: {Path}", path);
        }

        public override string Path => _path;

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Remove from tracking
            _parent.UntrackItem(_path);

            // Skip deletion if preserve flag is set
            if (_parent.ShouldPreserveTempFiles)
            {
                return;
            }

            try
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path, recursive: true);
                    _parent.Logger?.LogDebug("Cleaned up temporary directory: {Path}", _path);
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }

    /// <summary>
    /// Default implementation of <see cref="TempFile"/>.
    /// </summary>
    private sealed class DefaultTempFile : TempFile
    {
        private readonly string _path;
        private readonly bool _deleteParentDirectory;
        private readonly FileSystemService _parent;
        private bool _disposed;

        public DefaultTempFile(string path, bool deleteParentDirectory, FileSystemService parent)
        {
            _path = path;
            _deleteParentDirectory = deleteParentDirectory;
            _parent = parent;

            _parent.Logger?.LogDebug("Allocated temporary file: {Path}", path);
        }

        public override string Path => _path;

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Remove from tracking
            _parent.UntrackItem(_path);

            // Skip deletion if preserve flag is set
            if (_parent.ShouldPreserveTempFiles)
            {
                return;
            }

            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                    _parent.Logger?.LogDebug("Cleaned up temporary file: {Path}", _path);
                }

                if (_deleteParentDirectory)
                {
                    var parentDir = System.IO.Path.GetDirectoryName(_path);
                    if (parentDir is not null && Directory.Exists(parentDir))
                    {
                        Directory.Delete(parentDir, recursive: true);
                    }
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }
}

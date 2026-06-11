// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Service for managing Aspire directories.
/// </summary>
/// <remarks>
/// This service provides a centralized way to manage directories used by Aspire,
/// including temporary files, cache, and other storage needs.
/// </remarks>
[Experimental("ASPIREFILESYSTEM001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IFileSystemService
{
    /// <summary>
    /// Gets the temporary directory service for managing temporary files and directories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this instead of calling <see cref="Directory.CreateTempSubdirectory(string?)"/> directly
    /// to ensure consistent temp file management and enable testability.
    /// </para>
    /// </remarks>
    ITempFileSystemService TempDirectory { get; }
}

/// <summary>
/// Service for managing temporary directories and files within Aspire.
/// </summary>
[Experimental("ASPIREFILESYSTEM001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface ITempFileSystemService
{
    /// <summary>
    /// Creates and returns a temporary subdirectory.
    /// </summary>
    /// <param name="prefix">Optional prefix for the subdirectory name (e.g., "aspire").</param>
    /// <returns>A <see cref="TempDirectory"/> representing the created temporary subdirectory. Dispose to delete.</returns>
    /// <remarks>
    /// <para>
    /// This method creates a unique temporary subdirectory using the system temp folder.
    /// The directory is created immediately. Dispose the returned object to delete the directory.
    /// </para>
    /// <para>
    /// Use this instead of calling <see cref="Directory.CreateTempSubdirectory(string?)"/> directly.
    /// </para>
    /// </remarks>
    TempDirectory CreateTempSubdirectory(string? prefix = null);

    /// <summary>
    /// Creates a new temporary file and returns it.
    /// </summary>
    /// <param name="fileName">Optional file name for the temporary file (e.g., "config.json", "script.sh"). If null, uses a random name.</param>
    /// <returns>A <see cref="TempFile"/> representing the created temporary file. Dispose to delete.</returns>
    /// <remarks>
    /// <para>
    /// This method creates a new temporary file. If a file name is specified, it creates a temporary subdirectory
    /// and places the file with that name inside it. If no file name is specified, it uses <see cref="Path.GetTempFileName"/>.
    /// Dispose the returned object to delete the file.
    /// </para>
    /// <para>
    /// Use this instead of calling <see cref="Path.GetTempFileName"/> directly.
    /// </para>
    /// </remarks>
    TempFile CreateTempFile(string? fileName = null);
}

/// <summary>
/// Represents a temporary directory that will be deleted when disposed.
/// </summary>
[Experimental("ASPIREFILESYSTEM001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public abstract class TempDirectory : IDisposable
{
    /// <summary>
    /// Gets the full path to the temporary directory.
    /// </summary>
    public abstract string Path { get; }

    /// <summary>
    /// Deletes the temporary directory and all its contents.
    /// </summary>
    public abstract void Dispose();
}

/// <summary>
/// Represents a temporary file that will be deleted when disposed.
/// </summary>
[Experimental("ASPIREFILESYSTEM001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public abstract class TempFile : IDisposable
{
    /// <summary>
    /// Gets the full path to the temporary file.
    /// </summary>
    public abstract string Path { get; }

    /// <summary>
    /// Deletes the temporary file. When created with a file name, also deletes the parent directory.
    /// </summary>
    public abstract void Dispose();
}

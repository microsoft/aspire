// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Allocates temporary files and directories for Aspire.
/// </summary>
/// <remarks>
/// Consumers receive this service through dependency injection so the allocation location
/// can later be driven by configuration without changing every call site.
/// </remarks>
internal interface ITemporaryStorage
{
    /// <summary>
    /// Gets the root directory under which temporary storage is allocated.
    /// </summary>
    string GetTempPath();

    /// <summary>
    /// Creates a uniquely named, empty temporary file and returns its full path.
    /// </summary>
    string GetTempFileName();

    /// <summary>
    /// Creates a uniquely named temporary subdirectory.
    /// </summary>
    /// <param name="prefix">An optional prefix for the directory name.</param>
    DirectoryInfo CreateTempSubdirectory(string? prefix = null);
}

/// <summary>
/// Default <see cref="ITemporaryStorage"/> implementation backed by the system temporary directory.
/// </summary>
internal sealed class TemporaryStorage : ITemporaryStorage
{
    // This type is the sole production boundary for system temporary storage APIs.
#pragma warning disable RS0030

    /// <summary>
    /// Gets the operating system's temporary directory.
    /// </summary>
    /// <remarks>
    /// Use this only for guards that must recognize the system temporary root itself, regardless of
    /// where <see cref="ITemporaryStorage"/> allocates. Allocate storage through an injected instance.
    /// </remarks>
    internal static string SystemTempPath => Path.GetTempPath();

    public string GetTempPath() => Path.GetTempPath();

    public string GetTempFileName() => Path.GetTempFileName();

    public DirectoryInfo CreateTempSubdirectory(string? prefix = null)
        => Directory.CreateTempSubdirectory(prefix);
#pragma warning restore RS0030
}

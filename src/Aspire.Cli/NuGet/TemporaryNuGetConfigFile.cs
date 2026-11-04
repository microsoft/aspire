// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Materializes a supplied NuGet configuration and owns its temporary file lifetime.
/// </summary>
internal sealed class TemporaryNuGetConfigFile : IDisposable
{
    private readonly FileInfo _configFile;
    private readonly bool _ownsDirectory;
    private bool _disposed;

    private TemporaryNuGetConfigFile(FileInfo configFile, string cacheIdentity, bool ownsDirectory = true)
    {
        _configFile = configFile;
        CacheIdentity = cacheIdentity;
        _ownsDirectory = ownsDirectory;
    }

    public FileInfo ConfigFile => _configFile;

    public string CacheIdentity { get; private set; }

    public static Task<TemporaryNuGetConfigFile> CreateAsync(Action<string> writeConfig)
    {
        ArgumentNullException.ThrowIfNull(writeConfig);
        return CreateInDirectoryAsync(Directory.CreateTempSubdirectory("aspire-nuget-config"), writeConfig);
    }

    public static Task<TemporaryNuGetConfigFile> CreateAsync(DirectoryInfo parentDirectory, Action<string> writeConfig)
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(writeConfig);
        parentDirectory.Create();
        DirectoryInfo directory;
        do
        {
            directory = new DirectoryInfo(Path.Combine(
                parentDirectory.FullName,
                $".aspire-nuget-config-{Path.GetRandomFileName()}"));
        }
        while (directory.Exists);

        directory.Create();
        return CreateInDirectoryAsync(directory, writeConfig);
    }

    internal static async Task<TemporaryNuGetConfigFile> CreatePreviewAsync(
        FileInfo targetFile, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.Combine(
            targetFile.DirectoryName!, $".aspire-nuget-candidate-{Path.GetRandomFileName()}.config"));
        var created = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(file.FullName, options))
            {
                created = true;
                await stream.WriteAsync(content, cancellationToken);
            }
            return new TemporaryNuGetConfigFile(file, await ComputeCacheIdentityAsync(file), ownsDirectory: false);
        }
        catch
        {
            if (created)
            {
                file.Delete();
            }
            throw;
        }
    }

    private static async Task<TemporaryNuGetConfigFile> CreateInDirectoryAsync(DirectoryInfo directory, Action<string> writeConfig)
    {
        try
        {
            var configFile = new FileInfo(Path.Combine(directory.FullName, "NuGet.Config"));
            writeConfig(configFile.FullName);
            return new TemporaryNuGetConfigFile(
                configFile, await ComputeCacheIdentityAsync(configFile).ConfigureAwait(false));
        }
        catch
        {
            TryDeleteDirectory(directory.FullName);
            throw;
        }
    }

    public async Task RegenerateAsync(Action<string> writeConfig)
    {
        ArgumentNullException.ThrowIfNull(writeConfig);
        writeConfig(_configFile.FullName);
        CacheIdentity = await ComputeCacheIdentityAsync(_configFile).ConfigureAwait(false);
    }

    private static async Task<string> ComputeCacheIdentityAsync(FileInfo configFile)
    {
        await using var stream = configFile.OpenRead();
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
        document.Descendants("config").Elements("add")
            .Where(static element => string.Equals(
                (string?)element.Attribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase))
            .Remove();
        document.Descendants("config").Where(static section => !section.Elements().Any()).Remove();
        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        return Convert.ToHexString(XxHash3.Hash(bytes));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_ownsDirectory)
        {
            _configFile.Delete();
        }
        else if (_configFile.DirectoryName is { } directory)
        {
            TryDeleteDirectory(directory);
        }

        _disposed = true;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary file can remain locked by NuGet after a failed operation.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the original operation failure.
        }
    }
}

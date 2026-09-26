// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

#if ASPIRE_HOSTING_CORE
namespace Aspire.Hosting.Internal;
#else
namespace Aspire.Hosting.Dotnet;
#endif

internal static class MsBuildResponseFileFactory
{
    public static async Task<MsBuildResponseFile?> CreateAsync(
        IReadOnlyDictionary<string, string> properties,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(logger);

        if (properties.Count == 0)
        {
            return null;
        }

        var directory = Directory.CreateTempSubdirectory("aspire-msbuild-");
        var responseFilePath = Path.Combine(directory.FullName, "build-properties.rsp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                // Build properties are not a secret transport, but the response file still belongs only to this
                // process. Set the final mode atomically so another local user cannot read project-specific values.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            var stream = new FileStream(responseFilePath, options);
            await using var streamScope = stream.ConfigureAwait(false);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await using var writerScope = writer.ConfigureAwait(false);
            foreach (var (name, value) in properties)
            {
                // MSBuild's response-file tokenizer splits on all Unicode whitespace. Quoting the complete switch
                // keeps those characters in the property value; percent escaping handles embedded quotes,
                // backslashes, and line-breaking whitespace without relying on platform quoting rules.
                await writer.WriteLineAsync(
                    $"\"{CreatePropertyArgument(name, value)}\"".AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            return new MsBuildResponseFile(directory, responseFilePath, logger);
        }
        catch
        {
            TryDeleteDirectory(directory, logger);
            throw;
        }
    }

    public static string CreatePropertyArgument(string name, string value) =>
        $"--property:{EscapePropertyValue(name)}={EscapePropertyValue(value)}";

    internal static void TryDeleteDirectory(DirectoryInfo directory, ILogger logger)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to delete temporary MSBuild response-file directory '{DirectoryPath}'.", directory.FullName);
        }
    }

    private static string EscapePropertyValue(string value)
    {
        // MSBuild decodes %-escaped special characters in property values. Response files are line-oriented
        // command input, so quotes, backslashes, and ASCII whitespace are escaped before the complete switch is
        // quoted by the caller.
        // https://learn.microsoft.com/visualstudio/msbuild/msbuild-response-files
        // Escape '%' first so an existing sequence such as "%3B" remains literal.
        // https://learn.microsoft.com/visualstudio/msbuild/how-to-escape-special-characters-in-msbuild
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("$", "%24", StringComparison.Ordinal)
            .Replace("@", "%40", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal)
            .Replace("\\", "%5C", StringComparison.Ordinal)
            .Replace("\"", "%22", StringComparison.Ordinal)
            .Replace("'", "%27", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("\t", "%09", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
    }
}

internal sealed class MsBuildResponseFile(
    DirectoryInfo directory,
    string responseFilePath,
    ILogger logger) : IDisposable
{
    private DirectoryInfo? _directory = directory;

    public string Argument { get; } = $"@{responseFilePath}";

    internal string FilePath { get; } = responseFilePath;

    public void Dispose()
    {
        var directoryToDelete = Interlocked.Exchange(ref _directory, null);
        if (directoryToDelete is not null)
        {
            MsBuildResponseFileFactory.TryDeleteDirectory(directoryToDelete, logger);
        }
    }
}

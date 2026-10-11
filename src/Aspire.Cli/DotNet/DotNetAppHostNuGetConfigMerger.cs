// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Prepares and persists local .NET AppHost configuration changes from shared NuGet policy.
/// </summary>
internal sealed class DotNetAppHostNuGetConfigMerger(BundleNuGetService nuGetService)
{
    internal async Task CreateOrUpdateAsync(
        DirectoryInfo targetDirectory,
        NuGetConfiguration configuration,
        bool createIfMissing,
        string? globalPackagesFolder,
        Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback,
        CancellationToken cancellationToken)
    {
        var candidate = await PrepareAsync(
            targetDirectory, configuration, createIfMissing, globalPackagesFolder, cancellationToken);
        if (candidate is null ||
            confirmationCallback is not null &&
            !await confirmationCallback(
                candidate.TargetFile,
                candidate.GetOriginalDocument(),
                candidate.GetProposedDocument(),
                cancellationToken))
        {
            return;
        }

        await ApplyAsync(candidate, cancellationToken);
    }

    internal async Task<DotNetAppHostNuGetConfigMergerCandidate?> PrepareAsync(
        DirectoryInfo targetDirectory,
        NuGetConfiguration configuration,
        bool createIfMissing,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Overlay is null)
        {
            return null;
        }

        FileInfo? targetFile = null;
        var targetExists = targetDirectory.Exists &&
            TryFindNuGetConfigInDirectory(targetDirectory, out targetFile);
        if (!targetExists && !createIfMissing)
        {
            return null;
        }

        targetFile ??= new FileInfo(Path.Combine(targetDirectory.FullName, "nuget.config"));
        byte[]? originalContent = targetExists
            ? await File.ReadAllBytesAsync(targetFile.FullName, cancellationToken)
            : null;
        if (originalContent is not null && globalPackagesFolder == CliPathHelper.StagingNuGetPackagesFolderName)
        {
            var document = new XmlDocument();
            using var stream = new MemoryStream(originalContent, writable: false);
            document.Load(stream);
            if (document.SelectNodes("/configuration/config/add")!.OfType<XmlElement>()
                .Any(static element => string.Equals(element.GetAttribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase)))
            {
                globalPackagesFolder = null;
            }
        }

        if (!configuration.HasSourcePolicyChanges && globalPackagesFolder is null)
        {
            return null;
        }

        var proposedContent = await nuGetService.CreatePersistentNuGetConfigContentAsync(
            configuration,
            targetFile,
            originalContent is null ? default(ReadOnlyMemory<byte>?) : new ReadOnlyMemory<byte>(originalContent),
            globalPackagesFolder,
            cancellationToken);
        if (originalContent is not null && proposedContent.AsSpan().SequenceEqual(originalContent))
        {
            return null;
        }

        return new DotNetAppHostNuGetConfigMergerCandidate(targetFile, originalContent, proposedContent);
    }

    internal static Task ApplyAsync(
        DotNetAppHostNuGetConfigMergerCandidate candidate,
        CancellationToken cancellationToken)
        => ApplyAsync(candidate, static () => { }, cancellationToken);

    internal static async Task ApplyAsync(
        DotNetAppHostNuGetConfigMergerCandidate candidate,
        Action writeStarting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        candidate.TargetFile.Directory!.Create();

        // Check the baseline and write the approved snapshot under the same exclusive handle.
        // CreateNew also prevents overwriting a config created after preparation.
        await using var stream = candidate.TargetFile.Open(
            candidate.OriginalContent is null ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        if (candidate.OriginalContent is { } originalContent)
        {
            using var currentContent = new MemoryStream();
            await stream.CopyToAsync(currentContent, cancellationToken);
            if (!currentContent.ToArray().AsSpan().SequenceEqual(originalContent.Span))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.UpdateCandidateFileChangedFormat, candidate.TargetFile.FullName));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        writeStarting();
        stream.Position = 0;
        stream.SetLength(0);
        // Observe cancellation before committing, never after truncating the destination.
        await stream.WriteAsync(candidate.ProposedContent, CancellationToken.None);
    }

    internal static bool TryFindNuGetConfigInDirectory(
        DirectoryInfo directory,
        [NotNullWhen(true)] out FileInfo? nugetConfigFile)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var matches = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => string.Equals(file.Name, "nuget.config", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Multiple NuGet.config files found in '{directory.FullName}' differing only by case.");
        }

        nugetConfigFile = matches.SingleOrDefault();
        return matches.Length == 1;
    }
}

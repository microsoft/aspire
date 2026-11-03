// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge.Template;
using NuGet.Versioning;

namespace Aspire.Cli.Templating;

internal sealed partial class TemplateEngineFactory
{
    private async Task<TemplateResult> CreateWithComputedValuesAsync(
        TemplateCreator creator,
        ITemplateInfo template,
        string name,
        string outputPath,
        Dictionary<string, string?> parameters,
        TemplateHostConfiguration configuration,
        PackageChannel? channel,
        CancellationToken cancellationToken)
    {
        // Siblings inherit exactly the output directory's ancestor/user/machine settings.
        // Template-owned relative sources also resolve against the rendered template's files.
        // Neither render is published until resolution and configuration stability succeed.
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(outputPath))!.FullName;
        Directory.CreateDirectory(parent);
        using var preliminary = TemporaryCacheDirectory.Create(parent, "aspire-template", DeleteCheckout, DeleteLease);
        var result = await creator.InstantiateAsync(
            template, name, null, preliminary.FullName, parameters, cancellationToken: cancellationToken);
        if (result.Status != CreationResultStatus.Success)
        {
            return CreationFailed(result);
        }
        ReadNuGetConfigurationFiles(preliminary.FullName);
        if (channel is not null)
        {
            await templateNuGetConfigService.CreateNuGetConfigIfMissingAsync(channel, preliminary.FullName, outputPath, cancellationToken);
        }
        var originalConfigs = ReadNuGetConfigurationFiles(preliminary.FullName);
        foreach (var (symbol, source) in configuration.ValueSources)
        {
            if (!parameters.ContainsKey(symbol))
            {
                continue;
            }

            var workingDirectory = GetValueSourceWorkingDirectory(preliminary.FullName, source.WorkingDirectory);
            IReadOnlyList<NuGetVersion> versions;
            try
            {
                versions = await nuGetClient.GetPackageVersionsAsync(source.PackageId, workingDirectory, cancellationToken);
            }
            catch (NuGetOperationException ex)
            {
                logger.LogDebug("Template package version lookup failed: {Output}", ex.Output);
                throw new TemplateDiscoveryException(CliExitCodes.FailedToCreateNewProject,
                    string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplatePackageVersionLookupFailed, source.PackageId, symbol));
            }
            var selected = versions
                .Where(version => (source.AllowPrerelease || !version.IsPrerelease) &&
                    source.VersionRange.Satisfies(version) &&
                    (source.VersionRange.Float is null || source.VersionRange.Float.Satisfies(version)))
                .OrderByDescending(version => version, VersionComparer.VersionRelease)
                .FirstOrDefault();
            if (selected is null)
            {
                throw new TemplateDiscoveryException(CliExitCodes.FailedToCreateNewProject,
                    string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplatePackageVersionNotFound,
                        source.PackageId, source.VersionRange, source.AllowPrerelease, symbol));
            }
            parameters[symbol] = selected.ToNormalizedString();
        }

        // Reuse the same leased path so local package locations stay stable between renders.
        // All files here belong to this invocation, not to the user's output directory.
        DeleteTemplateDirectory(preliminary.FullName);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(preliminary.FullName);
        result = await interactionService.ShowStatusAsync(
            TemplatingStrings.CreatingNewProject,
            () => creator.InstantiateAsync(template, name, null, preliminary.FullName, parameters, cancellationToken: cancellationToken),
            emoji: KnownEmojis.Rocket);
        if (result.Status != CreationResultStatus.Success)
        {
            return CreationFailed(result);
        }
        ReadNuGetConfigurationFiles(preliminary.FullName);
        if (channel is not null)
        {
            await templateNuGetConfigService.CreateNuGetConfigIfMissingAsync(channel, preliminary.FullName, outputPath, cancellationToken);
        }
        var finalConfigs = ReadNuGetConfigurationFiles(preliminary.FullName);
        if (originalConfigs.Count != finalConfigs.Count ||
            originalConfigs.Any(pair => !finalConfigs.TryGetValue(pair.Key, out var content) || !pair.Value.AsSpan().SequenceEqual(content)))
        {
            // A computed version must not choose its own feeds or credentials. Rendering again
            // also detects indirect dependencies, such as conditional sources or renamed configs.
            throw new TemplateDiscoveryException(CliExitCodes.FailedToCreateNewProject, TemplatingStrings.TemplateNuGetConfigurationChanged);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outputError = OutputPathHelper.ValidateOutputPath(outputPath, executionContext.WorkingDirectory.FullName);
        if (outputError is not null)
        {
            interactionService.DisplayError(outputError);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
        if (Directory.Exists(outputPath))
        {
            // Keep an existing empty directory's identity, permissions, and current-directory users.
            // Copy without overwriting so a concurrent writer's files remain protected.
            FileSystemHelper.CopyDirectory(preliminary.FullName, outputPath);
        }
        else
        {
            preliminary.MoveTo(outputPath);
        }
        interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));
        return new TemplateResult(CliExitCodes.Success, outputPath);
    }

    private static string GetValueSourceWorkingDirectory(string root, string relativePath)
    {
        var path = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relativePath) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            !Directory.Exists(path))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateValueSourceWorkingDirectoryInvalid, relativePath));
        }

        return path;
    }

    private static Dictionary<string, byte[]> ReadNuGetConfigurationFiles(string root)
    {
        var configs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, TemplatingStrings.TemplateStagingLinkNotSupported);
                }
                if (entry is DirectoryInfo child)
                {
                    directories.Push(child);
                }
            }
            if (DotNetAppHostNuGetConfigMerger.TryFindNuGetConfigInDirectory(directory, out var config))
            {
                configs.Add(Path.GetRelativePath(root, config.FullName), File.ReadAllBytes(config.FullName));
            }
        }

        return configs;
    }
}

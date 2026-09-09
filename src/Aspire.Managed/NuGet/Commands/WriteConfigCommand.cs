// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using NuGet.Configuration;

namespace Aspire.Managed.NuGet.Commands;

internal static class WriteConfigCommand
{
    public static Command Create()
    {
        var command = new Command("write-config", "Writes an Aspire-owned NuGet configuration overlay");
        var requestOption = new Option<string>("--request")
        {
            Description = "Path to the JSON overlay request",
            Required = true
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Path to the generated NuGet.Config",
            Required = true
        };
        command.Options.Add(requestOption);
        command.Options.Add(outputOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var requestPath = parseResult.GetValue(requestOption)!;
            var outputPath = parseResult.GetValue(outputOption)!;
            return WriteAsync(requestPath, outputPath, cancellationToken);
        });

        return command;
    }

    private static async Task WriteAsync(
        string requestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var requestStream = File.OpenRead(requestPath);
        var request = await JsonSerializer.DeserializeAsync(
            requestStream,
            SettingsJsonContext.Default.NuGetConfigOverlayRequest,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The NuGet configuration overlay request was empty.");

        Write(request, outputPath);
    }

    internal static void Write(NuGetConfigOverlayRequest request, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputFile = new FileInfo(outputPath);
        outputFile.Directory?.Create();
        outputFile.Delete();

        var settings = new Settings(
            outputFile.DirectoryName!,
            outputFile.Name,
            isMachineWide: false);

        // A new Settings file starts with NuGet.org. An Aspire policy overlay must contain only
        // entries explicitly selected by the caller so ambient sources continue to come from the
        // normal configuration hierarchy.
        var defaultSources = settings
            .GetSection(ConfigurationConstants.PackageSources)?
            .Items
            .OfType<SourceItem>()
            .ToArray() ?? [];
        foreach (var defaultSource in defaultSources)
        {
            settings.Remove(ConfigurationConstants.PackageSources, defaultSource);
        }

        foreach (var source in request.Sources)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.PackageSources,
                new SourceItem(source.Key, source.Source));
        }

        if (request.ClearDisabledPackageSources)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.DisabledPackageSources,
                new ClearItem());
            foreach (var sourceKey in request.DisabledPackageSourceKeys)
            {
                settings.AddOrUpdate(
                    ConfigurationConstants.DisabledPackageSources,
                    new AddItem(sourceKey, "true"));
            }
        }

        if (request.PackageSourceMappings.Length > 0)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.PackageSourceMapping,
                new ClearItem());
            var mappings = request.PackageSourceMappings
                .Select(static mapping => new PackageSourceMappingSourceItem(
                    mapping.SourceKey,
                    mapping.Patterns.Select(static pattern => new PackagePatternItem(pattern))))
                .ToArray();
            new PackageSourceMappingProvider(settings, shouldSkipSave: true)
                .SavePackageSourceMappings(mappings);
        }

        if (!string.IsNullOrEmpty(request.GlobalPackagesFolder))
        {
            settings.AddOrUpdate(
                ConfigurationConstants.Config,
                new AddItem(
                    ConfigurationConstants.GlobalPackagesFolder,
                    request.GlobalPackagesFolder));
        }

        settings.SaveToDisk();
    }
}

internal sealed record NuGetConfigOverlayRequest(
    NuGetConfigSourceResult[] Sources,
    NuGetPackageSourceMappingResult[] PackageSourceMappings,
    bool ClearDisabledPackageSources,
    string[] DisabledPackageSourceKeys,
    string? GlobalPackagesFolder);

internal sealed record NuGetConfigSourceResult(string Key, string Source);

internal sealed record NuGetPackageSourceMappingResult(string SourceKey, string[] Patterns);

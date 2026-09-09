// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Configuration;

namespace Aspire.Managed.NuGet.Commands;

internal static class SettingsCommand
{
    public static Command Create()
    {
        var command = new Command("settings", "Describes the effective NuGet configuration hierarchy");
        var workingDirectoryOption = new Option<string>("--working-dir", "-w")
        {
            Description = "Working directory for NuGet.config discovery",
            Required = true
        };
        command.Options.Add(workingDirectoryOption);

        command.SetAction(parseResult =>
        {
            var workingDirectory = parseResult.GetValue(workingDirectoryOption)!;
            Console.WriteLine(JsonSerializer.Serialize(
                GetSettings(workingDirectory),
                SettingsJsonContext.Default.NuGetSettingsResult));
            return 0;
        });

        return command;
    }

    internal static NuGetSettingsResult GetSettings(string workingDirectory)
    {
        var settings = Settings.LoadDefaultSettings(
            workingDirectory,
            configFileName: null,
            new XPlatMachineWideSetting());
        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Select(static source => new NuGetSourceResult(
                source.Name,
                source.Source,
                source.IsEnabled))
            .ToArray();
        var packageSourceMappingEnabled = new PackageSourceMappingProvider(settings)
            .GetPackageSourceMappingItems()
            .Count > 0;

        return new NuGetSettingsResult(
            settings.GetConfigFilePaths().ToArray(),
            sources,
            packageSourceMappingEnabled);
    }
}

internal sealed record NuGetSettingsResult(
    string[] ConfigPaths,
    NuGetSourceResult[] Sources,
    bool PackageSourceMappingEnabled);

internal sealed record NuGetSourceResult(string Name, string Source, bool IsEnabled);

[JsonSerializable(typeof(NuGetSettingsResult))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

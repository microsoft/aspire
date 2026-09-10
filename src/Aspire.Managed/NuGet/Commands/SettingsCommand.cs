// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Shared;
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
                GetSettings(workingDirectory, ReadIdentityKey()),
                SettingsJsonContext.Default.NuGetSettingsResult));
            return 0;
        });

        return command;
    }

    internal static NuGetSettingsResult GetSettings(string workingDirectory, byte[] identityKey)
    {
        ArgumentNullException.ThrowIfNull(identityKey);

        var settings = Settings.LoadDefaultSettings(
            workingDirectory,
            configFileName: null,
            new XPlatMachineWideSetting());
        var packageSources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .ToArray();
        var sources = packageSources
            .Select(source => CreateSourceResult(source, identityKey))
            .ToArray();
        var sensitiveSourceValues = packageSources
            .Select(static source => source.Source)
            .Where(NuGetSourceIdentity.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var packageSourceMappings = new PackageSourceMappingProvider(settings)
            .GetPackageSourceMappingItems()
            .Select(static mapping => new NuGetPackageSourceMappingResult(
                mapping.Key,
                mapping.Patterns.Select(static pattern => pattern.Pattern).ToArray()))
            .ToArray();
        var disabledPackageSourceKeys = settings
            .GetSection(ConfigurationConstants.DisabledPackageSources)?
            .Items
            .OfType<AddItem>()
            .Select(static item => item.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var credentialSourceKeys = settings
            .GetSection(ConfigurationConstants.CredentialsSectionName)?
            .Items
            .OfType<CredentialsItem>()
            .Select(static item => item.ElementName) ?? [];
        var clientCertificateSourceKeys = settings
            .GetSection(ConfigurationConstants.ClientCertificates)?
            .Items
            .OfType<ClientCertItem>()
            .Select(static item => item.PackageSource) ?? [];
        var reservedPackageSourceKeys = sources
            .Select(static source => source.Name)
            .Concat(packageSourceMappings.Select(static mapping => mapping.SourceKey))
            .Concat(disabledPackageSourceKeys)
            .Concat(credentialSourceKeys)
            .Concat(clientCertificateSourceKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NuGetSettingsResult(
            settings.GetConfigFilePaths().ToArray(),
            sources,
            sensitiveSourceValues,
            packageSourceMappings.Length > 0,
            packageSourceMappings,
            disabledPackageSourceKeys,
            reservedPackageSourceKeys);
    }

    private static NuGetSourceResult CreateSourceResult(PackageSource source, byte[] identityKey)
        => new(
            source.Name,
            NuGetSourceIdentity.Compute(source.Source, identityKey),
            source.IsEnabled);

    private static byte[] ReadIdentityKey()
    {
        var encodedKey = Environment.GetEnvironmentVariable(NuGetSourceIdentity.KeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException("The NuGet source identity key was not provided.");
        }

        try
        {
            var key = Convert.FromBase64String(encodedKey);
            if (key.Length != NuGetSourceIdentity.KeySizeInBytes)
            {
                throw new InvalidOperationException("The NuGet source identity key has an invalid length.");
            }

            return key;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The NuGet source identity key is invalid.", ex);
        }
    }
}

internal sealed record NuGetSettingsResult(
    string[] ConfigPaths,
    NuGetSourceResult[] Sources,
    string[] SensitiveSourceValues,
    bool PackageSourceMappingEnabled,
    NuGetPackageSourceMappingResult[] PackageSourceMappings,
    string[] DisabledPackageSourceKeys,
    string[] ReservedPackageSourceKeys);

internal sealed record NuGetSourceResult(
    string Name,
    string Identity,
    bool IsEnabled);

[JsonSerializable(typeof(NuGetConfigOverlayRequest))]
[JsonSerializable(typeof(NuGetSettingsResult))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

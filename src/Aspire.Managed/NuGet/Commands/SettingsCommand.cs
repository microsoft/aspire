// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
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
                SettingsJsonContext.Default.NuGetSettingsResponse));
            return 0;
        });

        return command;
    }

    internal static NuGetSettingsResponse GetSettings(string workingDirectory, byte[] identityKey)
    {
        ArgumentNullException.ThrowIfNull(identityKey);

        var settings = Settings.LoadDefaultSettings(
            workingDirectory,
            configFileName: null,
            new XPlatMachineWideSetting());
        var packageSourceProvider = new PackageSourceProvider(settings);
        var packageSources = packageSourceProvider.LoadPackageSources().ToArray();
        var auditSources = packageSourceProvider.LoadAuditSources().ToArray();
        var sources = packageSources
            .Select(source => CreateSourceResult(source, identityKey))
            .ToArray();
        var sensitiveSourceValues = packageSources
            .Concat(auditSources)
            .Select(static source => source.Source)
            .Where(NuGetSourceIdentity.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var packageSourceMappings = new PackageSourceMappingProvider(settings)
            .GetPackageSourceMappingItems()
            .Select(static mapping => new NuGetPackageSourceMapping(
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

        return new NuGetSettingsResponse(
            settings.GetConfigFilePaths().ToArray(),
            ComputeCacheIdentity(settings, packageSources, auditSources, packageSourceMappings),
            sources,
            sensitiveSourceValues,
            packageSourceMappings.Length > 0,
            packageSourceMappings,
            disabledPackageSourceKeys,
            reservedPackageSourceKeys);
    }

    internal static string ComputeCacheIdentity(
        ISettings settings,
        IReadOnlyList<PackageSource> packageSources,
        IReadOnlyList<PackageSource> auditSources,
        IReadOnlyList<NuGetPackageSourceMapping> packageSourceMappings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packageSources);
        ArgumentNullException.ThrowIfNull(auditSources);
        ArgumentNullException.ThrowIfNull(packageSourceMappings);

        var hash = new XxHash3();

        foreach (var configPath in settings.GetConfigFilePaths())
        {
            AppendValue(hash, "config-path");
            AppendValue(hash, configPath);
        }

        AppendPackageSources(hash, "package-source", packageSources);
        AppendPackageSources(hash, "audit-source", auditSources);

        var signatureValidationMode = settings
            .GetSection("config")?
            .Items
            .OfType<AddItem>()
            .LastOrDefault(static item => string.Equals(item.Key, "signatureValidationMode", StringComparison.OrdinalIgnoreCase))?
            .Value;
        if (signatureValidationMode is not null)
        {
            // This setting changes whether a package is accepted during restore. Hash the effective
            // value without serializing unrelated config entries, which can contain credentials.
            AppendValue(hash, "signature-validation-mode");
            AppendValue(hash, signatureValidationMode);
        }

        foreach (var mapping in packageSourceMappings)
        {
            AppendValue(hash, "package-source-mapping");
            AppendValue(hash, mapping.SourceKey);
            foreach (var pattern in mapping.Patterns)
            {
                AppendValue(hash, pattern);
            }
        }

        var pathContext = NuGetPathContext.Create(settings);
        AppendValue(hash, "global-packages");
        AppendValue(hash, pathContext.UserPackageFolder);
        foreach (var fallbackPackageFolder in pathContext.FallbackPackageFolders)
        {
            AppendValue(hash, "fallback-packages");
            AppendValue(hash, fallbackPackageFolder);
        }

        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static void AppendPackageSources(
        XxHash3 hash,
        string kind,
        IReadOnlyList<PackageSource> sources)
    {
        foreach (var source in sources)
        {
            AppendValue(hash, kind);
            AppendValue(hash, source.Name);
            AppendValue(hash, source.Source);
            AppendValue(hash, source.IsEnabled.ToString(CultureInfo.InvariantCulture));
            AppendValue(hash, source.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
            AppendValue(hash, source.AllowInsecureConnections.ToString(CultureInfo.InvariantCulture));
            AppendValue(hash, source.DisableTLSCertificateValidation.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendValue(XxHash3 hash, string value)
    {
        hash.Append(Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture)));
        hash.Append(":"u8);
        hash.Append(Encoding.UTF8.GetBytes(value));
        hash.Append("\n"u8);
    }

    private static NuGetSourceInfo CreateSourceResult(PackageSource source, byte[] identityKey)
        => new(
            source.Name,
            NuGetSourceIdentity.Compute(source.Source, identityKey),
            source.IsEnabled,
            source.Credentials is not null,
            source.ClientCertificates is { Count: > 0 });

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

[JsonSerializable(typeof(NuGetConfigOverlayRequest))]
[JsonSerializable(typeof(NuGetSettingsResponse))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Aspire.HealthModels;

namespace Aspire.Hosting.Azure;

/// <summary>A publish-only Azure health model and managed Prometheus ingestion bundle.</summary>
[AspireExport]
[Experimental("ASPIREAZUREHEALTH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureHealthModelResource : AzureBicepResource
{
    /// <summary>Initializes a health-model resource without reading files or contacting Azure.</summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="definitionFile">The absolute path of the project-owned model definition.</param>
    public AzureHealthModelResource(string name, string definitionFile) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionFile);
        DefinitionFile = definitionFile;
    }

    /// <summary>The model definition consumed during publish and deployment.</summary>
    public string DefinitionFile { get; }

    internal Action<HealthModelDocument>? ValidateBindings { get; init; }

    /// <summary>The provisioned Azure health model resource ID.</summary>
    public BicepOutputReference HealthModelId => new("healthModelId", this);
    /// <summary>The Azure Monitor workspace resource ID.</summary>
    public BicepOutputReference WorkspaceId => new("workspaceId", this);
    /// <summary>The data collection endpoint resource ID.</summary>
    public BicepOutputReference DataCollectionEndpointId => new("dataCollectionEndpointId", this);
    /// <summary>The data collection rule resource ID.</summary>
    public BicepOutputReference DataCollectionRuleId => new("dataCollectionRuleId", this);
    /// <summary>The authenticated Prometheus remote-write URL, including the rule's immutable ID.</summary>
    public BicepOutputReference RemoteWriteEndpoint => new("remoteWriteEndpoint", this);
    /// <summary>The managed identity to attach to the remote-write collector.</summary>
    public BicepOutputReference CollectorIdentityId => new("collectorIdentityId", this);
    /// <summary>The collector's user-assigned managed identity client ID.</summary>
    public BicepOutputReference CollectorClientId => new("collectorClientId", this);

    /// <inheritdoc/>
    public override string GetBicepTemplateString()
    {
        try
        {
            var file = new FileInfo(DefinitionFile);
            if (!file.Exists)
            {
                throw new FileNotFoundException("Export the model from Dashboard > Health and save it in the AppHost project.", DefinitionFile);
            }
            if (file.Length > HealthModelContract.MaxFileSize)
            {
                throw new InvalidDataException("The health model definition exceeds the supported file size.");
            }

            var document = HealthModelContract.Deserialize(File.ReadAllText(DefinitionFile));
            ValidateBindings?.Invoke(document);
            return HealthModelBicepGenerator.Generate(document);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            throw new DistributedApplicationException(
                $"Cannot publish health model '{Name}' from '{DefinitionFile}'. Export a valid version 1 definition from Dashboard > Health. {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public override BicepTemplateFile GetBicepTemplateFile(string? directory = null, bool deleteTemporaryFileOnDispose = true)
    {
        // Generate before creating the temporary directory so invalid definitions leave no artifacts.
        var content = GetBicepTemplateString();
        var temporary = directory is null;
        directory ??= Directory.CreateTempSubdirectory("aspire-health-model-").FullName;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Name.ToLowerInvariant()}.module.bicep");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new BicepTemplateFile(path, temporary && deleteTemporaryFileOnDispose);
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.KeyVault;

namespace Aspire.Hosting;

/// <summary>
/// Extensions for working with <see cref="AzureProvisioningResource"/> and related types.
/// </summary>
public static class AzureProvisioningResourceExtensions
{
    /// <summary>
    /// Adds an Azure provisioning resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource being added.</param>
    /// <param name="configureInfrastructure">A callback used to configure the infrastructure resource.</param>
    /// <returns>A resource builder for the <see cref="AzureProvisioningResource"/> that can be used for further configuration.</returns>
    [AspireExport(Description = "Adds an Azure provisioning resource to the application model")]
    public static IResourceBuilder<AzureProvisioningResource> AddAzureInfrastructure(this IDistributedApplicationBuilder builder, [ResourceName] string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    {
        builder.AddAzureProvisioning();

        var resource = new AzureProvisioningResource(name, configureInfrastructure);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Configures the Azure provisioning resource <see cref="Infrastructure"/>.
    /// </summary>
    /// <typeparam name="T">Type of the <see cref="AzureProvisioningResource"/> resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Configures the Azure provisioning infrastructure callback")]
    public static IResourceBuilder<T> ConfigureInfrastructure<T>(this IResourceBuilder<T> builder, Action<AzureResourceInfrastructure> configure)
        where T : AzureProvisioningResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.ConfigureInfrastructure += configure;
        return builder;
    }

    /// <summary>
    /// Gets or creates a <see cref="KeyVaultSecret"/> resource in the specified <see cref="AzureResourceInfrastructure"/>
    /// for the given <see cref="IAzureKeyVaultSecretReference"/>.
    /// <para>
    /// If the referenced Key Vault or secret does not already exist in the infrastructure, they will be created and added.
    /// This allows referencing secrets that are provisioned outside of the current deployment.
    /// </para>
    /// </summary>
    /// <param name="secretReference">The <see cref="IAzureKeyVaultSecretReference"/> representing the Key Vault secret to reference.</param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> in which to locate or add the <see cref="KeyVaultSecret"/>.</param>
    /// <returns>
    /// The <see cref="KeyVaultSecret"/> instance corresponding to the given secret reference.
    /// </returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "KeyVaultSecret is an Azure.Provisioning type not compatible with ATS.")]
    public static KeyVaultSecret AsKeyVaultSecret(this IAzureKeyVaultSecretReference secretReference, AzureResourceInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(secretReference);
        ArgumentNullException.ThrowIfNull(infrastructure);

        var resources = infrastructure.GetProvisionableResources();

        var kvName = secretReference.Resource.GetBicepIdentifier();

        var kv = resources.OfType<KeyVaultService>().SingleOrDefault(kv => kv.BicepIdentifier == kvName);

        if (kv is null)
        {
            kv = KeyVaultService.FromExisting(kvName);

            if (!AzureProvisioningResource.TryApplyExistingResourceAnnotation(
                secretReference.Resource,
                infrastructure,
                kv))
            {
                kv.Name = secretReference.Resource.NameOutputReference.AsProvisioningParameter(infrastructure);
            }

            infrastructure.Add(kv);
        }

        var kvsName = Infrastructure.NormalizeBicepIdentifier($"{kv.BicepIdentifier}_{secretReference.SecretName}");
        var kvs = resources.OfType<KeyVaultSecret>().SingleOrDefault(kvSecret => kvSecret.BicepIdentifier == kvsName);

        if (kvs is null)
        {
            // Ensure the secret reference is tracked in the infrastructure for dependency discovery
            infrastructure.AspireResource.References.Add(secretReference);

            kvs = KeyVaultSecret.FromExisting(kvsName);
            kvs.Name = secretReference.SecretName;
            kvs.Parent = kv;
            infrastructure.Add(kvs);
        }

        return kvs;
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to <paramref name="parameterResourceBuilder"/>.
    /// </summary>
    /// <param name="parameterResourceBuilder">
    /// The <see cref="IResourceBuilder{ParameterResource}"/> that represents a parameter in the <see cref="Aspire.Hosting.ApplicationModel" />
    /// to get or create a corresponding <see cref="ProvisioningParameter"/>.
    /// </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>
    /// This method is not available in polyglot app hosts.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The 'this' arguments are mutually exclusive")]
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this IResourceBuilder<ParameterResource> parameterResourceBuilder, AzureResourceInfrastructure infrastructure, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(parameterResourceBuilder);
        ArgumentNullException.ThrowIfNull(infrastructure);

        return parameterResourceBuilder.Resource.AsProvisioningParameter(infrastructure, parameterName);
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to the given <paramref name="manifestExpressionProvider"/>.
    /// </summary>
    /// <param name="manifestExpressionProvider">The <see cref="IManifestExpressionProvider"/> that represents the value to use for the <see cref="ProvisioningParameter"/>. </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <param name="isSecure">Indicates whether the parameter is secure.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this IManifestExpressionProvider manifestExpressionProvider, AzureResourceInfrastructure infrastructure, string? parameterName = null, bool? isSecure = null)
    {
        ArgumentNullException.ThrowIfNull(manifestExpressionProvider);
        ArgumentNullException.ThrowIfNull(infrastructure);

        parameterName ??= GetNameFromValueExpression(manifestExpressionProvider);

        infrastructure.AspireResource.Parameters[parameterName] = manifestExpressionProvider;

        return GetOrAddParameter(infrastructure, parameterName, isSecure);
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to <paramref name="parameterResource"/>.
    /// </summary>
    /// <param name="parameterResource">
    /// The <see cref="ParameterResource"/> that represents a parameter in the <see cref="Aspire.Hosting.ApplicationModel" />
    /// to get or create a corresponding <see cref="ProvisioningParameter"/>.
    /// </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>
    /// This method is not available in polyglot app hosts.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The 'this' arguments are mutually exclusive")]
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this ParameterResource parameterResource, AzureResourceInfrastructure infrastructure, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(parameterResource);
        ArgumentNullException.ThrowIfNull(infrastructure);

        parameterName ??= Infrastructure.NormalizeBicepIdentifier(parameterResource.Name);

        infrastructure.AspireResource.Parameters[parameterName] = parameterResource;

        return GetOrAddParameter(infrastructure, parameterName, parameterResource.Secret);
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to <paramref name="outputReference"/>.
    /// </summary>
    /// <param name="outputReference">
    /// The <see cref="BicepOutputReference"/> that contains the value to use for the <see cref="ProvisioningParameter"/>.
    /// </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>
    /// This method is not available in polyglot app hosts.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The 'this' arguments are mutually exclusive")]
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this BicepOutputReference outputReference, AzureResourceInfrastructure infrastructure, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(outputReference);
        ArgumentNullException.ThrowIfNull(infrastructure);

        parameterName ??= GetNameFromValueExpression(outputReference);

        infrastructure.AspireResource.Parameters[parameterName] = outputReference;

        return GetOrAddParameter(infrastructure, parameterName);
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to <paramref name="endpointReference"/>.
    /// </summary>
    /// <param name="endpointReference">
    /// The <see cref="EndpointReference"/> to use for the value of the <see cref="ProvisioningParameter"/>.
    /// </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>
    /// This method is not available in polyglot app hosts.
    /// </remarks>
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this EndpointReference endpointReference, AzureResourceInfrastructure infrastructure, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(endpointReference);
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        infrastructure.AspireResource.Parameters[parameterName] = endpointReference;

        return GetOrAddParameter(infrastructure, parameterName);
    }

    /// <summary>
    /// Creates a new <see cref="ProvisioningParameter"/> in <paramref name="infrastructure"/>, or reuses an existing bicep parameter if one with
    /// the same name already exists, that corresponds to <paramref name="expression"/>.
    /// </summary>
    /// <param name="expression">
    /// The <see cref="ReferenceExpression"/> that represents the value to use for the <see cref="ProvisioningParameter"/>.
    /// </param>
    /// <param name="infrastructure">The <see cref="AzureResourceInfrastructure"/> that contains the <see cref="ProvisioningParameter"/>.</param>
    /// <param name="parameterName">The name of the parameter to be assigned.</param>
    /// <returns>
    /// The corresponding <see cref="ProvisioningParameter"/> that was found or newly created.
    /// </returns>
    /// <remarks>
    /// This method is not available in polyglot app hosts.
    /// </remarks>
    [AspireExportIgnore(Reason = "ProvisioningParameter is an Azure.Provisioning type not compatible with ATS.")]
    public static ProvisioningParameter AsProvisioningParameter(this ReferenceExpression expression, AzureResourceInfrastructure infrastructure, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        infrastructure.AspireResource.Parameters[parameterName] = expression;

        return GetOrAddParameter(infrastructure, parameterName);
    }

    private static ProvisioningParameter GetOrAddParameter(AzureResourceInfrastructure infrastructure, string parameterName, bool? isSecure = null)
    {
        var parameter = infrastructure.GetParameters().FirstOrDefault(p => p.BicepIdentifier == parameterName);
        if (parameter is null)
        {
            parameter = new ProvisioningParameter(parameterName, typeof(string));
            if (isSecure.HasValue)
            {
                parameter.IsSecure = isSecure.Value;
            }

            infrastructure.Add(parameter);
        }

        return parameter;
    }

    private static string GetNameFromValueExpression(IManifestExpressionProvider ep)
    {
        var parameterName = ep.ValueExpression.Replace("{", "").Replace("}", "").Replace(".", "_").Replace("-", "_").ToLowerInvariant();

        if (parameterName[0] == '_')
        {
            parameterName = parameterName[1..];
        }
        return Infrastructure.NormalizeBicepIdentifier(parameterName);
    }
}

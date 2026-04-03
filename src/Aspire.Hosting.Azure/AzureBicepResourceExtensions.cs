// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Bicep resources to the application model.
/// </summary>
public static class AzureBicepResourceExtensions
{
    /// <summary>
    /// Adds an Azure Bicep resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the deployment name.</param>
    /// <param name="bicepFile">The path to the bicep file on disk. This path is relative to the apphost's project directory.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds an Azure Bicep template resource from a file")]
    public static IResourceBuilder<AzureBicepResource> AddBicepTemplate(this IDistributedApplicationBuilder builder, [ResourceName] string name, string bicepFile)
    {
        builder.AddAzureProvisioning();

        var path = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, bicepFile));
        var resource = new AzureBicepResource(name, templateFile: path, templateString: null);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds an Azure Bicep resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the deployment name.</param>
    /// <param name="bicepContent">A string that represents a snippet of bicep.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds an Azure Bicep template resource from inline Bicep content")]
    public static IResourceBuilder<AzureBicepResource> AddBicepTemplateString(this IDistributedApplicationBuilder builder, [ResourceName] string name, string bicepContent)
    {
        builder.AddAzureProvisioning();

        var resource = new AzureBicepResource(name, templateFile: null, templateString: bicepContent);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Gets a reference to an output from a bicep template.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">Name of the output.</param>
    /// <returns>A <see cref="BicepOutputReference"/> that represents the output.</returns>
    [AspireExport(Description = "Gets an output reference from an Azure Bicep template resource")]
    public static BicepOutputReference GetOutput(this IResourceBuilder<AzureBicepResource> builder, string name)
    {
        return new BicepOutputReference(name, builder.Resource);
    }

    /// <summary>
    /// Gets a reference to a secret output from a bicep template. This is an output that is written to a keyvault using the "keyVaultName" convention.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the secret output.</param>
    /// <returns>A <see cref="BicepSecretOutputReference"/> that represents the output.</returns>
    [Obsolete("GetSecretOutput is obsolete. Use IAzureKeyVaultResource.GetSecret instead.")]
    public static BicepSecretOutputReference GetSecretOutput(this IResourceBuilder<AzureBicepResource> builder, string name)
    {
        return new BicepSecretOutputReference(name, builder.Resource);
    }

    /// <summary>
    /// Adds an environment variable to the resource with the value of the output from the bicep template.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="bicepOutputReference">The reference to the bicep output.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEnvironment dispatcher export.")]
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, BicepOutputReference bicepOutputReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(name, (IExpressionValue)bicepOutputReference);
    }

    // Keep these ATS-only aliases for backward compatibility with existing polyglot app hosts.
    // Remove them once callers have migrated to the unified withEnvironment(...) export.
    // Tracking issue: https://github.com/microsoft/aspire/issues/15734
    /// <summary>
    /// Obsolete ATS alias for <see cref="WithEnvironment{T}(IResourceBuilder{T}, string, BicepOutputReference)"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="bicepOutputReference">The reference to the Bicep output.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("ATS compatibility shim. Use withEnvironment instead.")]
    [AspireExport("withEnvironmentFromOutput", Description = "Sets an environment variable from a Bicep output reference")]
    internal static IResourceBuilder<T> WithEnvironmentFromOutputShim<T>(this IResourceBuilder<T> builder, string name, BicepOutputReference bicepOutputReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(name, bicepOutputReference);
    }

    /// <summary>
    /// Adds an environment variable to the resource with the value of the secret output from the bicep template.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="bicepOutputReference">The reference to the bicep output.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("BicepSecretOutputReference is no longer supported. Use WithEnvironment(IAzureKeyVaultSecretReference) instead.")]
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, BicepSecretOutputReference bicepOutputReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables[name] = bicepOutputReference;
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource with the value of the key vault secret.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="secretReference">The reference to the key vault secret.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEnvironment dispatcher export.")]
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, IAzureKeyVaultSecretReference secretReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(name, (IExpressionValue)secretReference);
    }

    /// <summary>
    /// Obsolete ATS alias for <see cref="WithEnvironment{T}(IResourceBuilder{T}, string, IAzureKeyVaultSecretReference)"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="secretReference">The key vault secret reference.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("ATS compatibility shim. Use withEnvironment instead.")]
    [AspireExport("withEnvironmentFromKeyVaultSecret", Description = "Sets an environment variable from an Azure Key Vault secret reference")]
    internal static IResourceBuilder<T> WithEnvironmentFromKeyVaultSecretShim<T>(this IResourceBuilder<T> builder, string name, IAzureKeyVaultSecretReference secretReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(name, secretReference);
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a Bicep parameter without a value")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);
        builder.Resource.Parameters[name] = null;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterStringValue", Description = "Adds a Bicep parameter with a string value")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, string value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterStringValues", Description = "Adds a Bicep parameter with a string list value")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IEnumerable<string> value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "JsonNode values are not ATS-compatible.")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, JsonNode value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="valueCallback">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Func<object?> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, Func<object?> valueCallback)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);
        builder.Resource.Parameters[name] = valueCallback;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterFromParameter", Description = "Adds a Bicep parameter from a parameter resource builder")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ParameterResource> value)
        where T : AzureBicepResource
    {
        return builder.WithParameter(name, value.Resource);
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <see cref="WithParameter{T}(IResourceBuilder{T}, string, IResourceBuilder{ParameterResource})"/> overload instead.</remarks>
    [AspireExportIgnore(Reason = "Raw ParameterResource overload; use the IResourceBuilder<ParameterResource> overload instead.")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, ParameterResource value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);

        builder.WithReferenceRelationship(value);

        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterFromConnectionString", Description = "Adds a Bicep parameter from a connection string resource builder")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<IResourceWithConnectionString> value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);

        builder.WithReferenceRelationship(value.Resource);

        builder.Resource.Parameters[name] = value.Resource;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterFromOutput", Description = "Adds a Bicep parameter from another Bicep output reference")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, BicepOutputReference value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);

        builder.WithReferenceRelationship(value.Resource);

        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterFromReferenceExpression", Description = "Adds a Bicep parameter from a reference expression")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, ReferenceExpression value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);

        builder.WithReferenceRelationship(value);

        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withParameterFromEndpoint", Description = "Adds a Bicep parameter from an endpoint reference")]
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, EndpointReference value)
        where T : AzureBicepResource
    {
        BicepIdentifierHelpers.ThrowIfInvalid(name);

        builder.WithReferenceRelationship(value.Resource);

        builder.Resource.Parameters[name] = value;
        return builder;
    }
}

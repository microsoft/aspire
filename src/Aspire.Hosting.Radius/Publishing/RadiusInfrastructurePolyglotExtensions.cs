// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004

using Aspire.Hosting.Radius.Publishing.Constructs;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Provides ATS-compatible adapters for customizing generated Radius infrastructure.
/// </summary>
internal static class RadiusInfrastructurePolyglotExtensions
{
    /// <summary>
    /// Adds a custom Radius resource type instance to the generated infrastructure.
    /// </summary>
    /// <param name="options">The generated Radius infrastructure.</param>
    /// <param name="bicepIdentifier">The Bicep identifier for the resource.</param>
    /// <param name="resourceType">The Radius resource type.</param>
    /// <param name="apiVersion">The Radius resource API version.</param>
    /// <returns>The added resource type construct.</returns>
    [AspireExport]
    internal static RadiusResourceTypeConstruct AddResourceTypeInstance(
        this RadiusInfrastructureOptions options,
        string bicepIdentifier,
        string resourceType,
        string apiVersion)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resource = new RadiusResourceTypeConstruct(bicepIdentifier, resourceType, apiVersion);
        options.ResourceTypeInstances.Add(resource);
        return resource;
    }

    /// <summary>
    /// Sets the deployed name of a Radius environment construct.
    /// </summary>
    /// <param name="environment">The Radius environment construct.</param>
    /// <param name="name">The deployed environment name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusEnvironmentConstruct WithEnvironmentName(
        this RadiusEnvironmentConstruct environment,
        string name)
    {
        environment.EnvironmentName = name;
        return environment;
    }

    /// <summary>
    /// Sets the Kubernetes namespace of a Radius environment construct.
    /// </summary>
    /// <param name="environment">The Radius environment construct.</param>
    /// <param name="kubernetesNamespace">The Kubernetes namespace.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusEnvironmentConstruct WithKubernetesNamespace(
        this RadiusEnvironmentConstruct environment,
        string kubernetesNamespace)
    {
        environment.KubernetesNamespace = kubernetesNamespace;
        return environment;
    }

    /// <summary>
    /// Sets the Azure provider fields of a Radius environment construct.
    /// </summary>
    /// <param name="environment">The Radius environment construct.</param>
    /// <param name="subscriptionId">The Azure subscription identifier.</param>
    /// <param name="resourceGroupName">The Azure resource group name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusEnvironmentConstruct WithEnvironmentAzureProvider(
        this RadiusEnvironmentConstruct environment,
        string subscriptionId,
        string resourceGroupName)
    {
        environment.AzureSubscriptionId = subscriptionId;
        environment.AzureResourceGroupName = resourceGroupName;
        return environment;
    }

    /// <summary>
    /// Sets the AWS provider fields of a Radius environment construct.
    /// </summary>
    /// <param name="environment">The Radius environment construct.</param>
    /// <param name="accountId">The AWS account identifier.</param>
    /// <param name="region">The AWS region.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusEnvironmentConstruct WithEnvironmentAwsProvider(
        this RadiusEnvironmentConstruct environment,
        string accountId,
        string region)
    {
        environment.AwsAccountId = accountId;
        environment.AwsRegion = region;
        return environment;
    }

    /// <summary>
    /// Adds a recipe pack reference to a Radius environment construct.
    /// </summary>
    /// <param name="environment">The Radius environment construct.</param>
    /// <param name="recipePackIdentifier">The Bicep identifier of the recipe pack.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusEnvironmentConstruct WithEnvironmentRecipePack(
        this RadiusEnvironmentConstruct environment,
        string recipePackIdentifier)
    {
        environment.RecipePacks.Add(ResourceIdExpression(recipePackIdentifier));
        return environment;
    }

    /// <summary>
    /// Sets the deployed name of a Radius application construct.
    /// </summary>
    /// <param name="application">The Radius application construct.</param>
    /// <param name="name">The deployed application name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusApplicationConstruct WithApplicationName(
        this RadiusApplicationConstruct application,
        string name)
    {
        application.ApplicationName = name;
        return application;
    }

    /// <summary>
    /// Sets the environment reference of a Radius application construct.
    /// </summary>
    /// <param name="application">The Radius application construct.</param>
    /// <param name="environmentIdentifier">The Bicep identifier of the environment.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusApplicationConstruct WithApplicationEnvironment(
        this RadiusApplicationConstruct application,
        string environmentIdentifier)
    {
        application.EnvironmentId = ResourceIdExpression(environmentIdentifier);
        return application;
    }

    /// <summary>
    /// Sets the deployed name of a Radius recipe pack construct.
    /// </summary>
    /// <param name="recipePack">The Radius recipe pack construct.</param>
    /// <param name="name">The deployed recipe pack name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusRecipePackConstruct WithRecipePackName(
        this RadiusRecipePackConstruct recipePack,
        string name)
    {
        recipePack.PackName = name;
        return recipePack;
    }

    /// <summary>
    /// Adds or replaces a recipe in a Radius recipe pack construct.
    /// </summary>
    /// <param name="recipePack">The Radius recipe pack construct.</param>
    /// <param name="resourceType">The Radius resource type served by the recipe.</param>
    /// <param name="recipeKind">The recipe kind.</param>
    /// <param name="recipeLocation">The recipe location.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusRecipePackConstruct WithRecipe(
        this RadiusRecipePackConstruct recipePack,
        string resourceType,
        string recipeKind,
        string recipeLocation)
    {
        recipePack.Recipes[resourceType] = new RecipeEntryConstruct
        {
            RecipeKind = recipeKind,
            RecipeLocation = recipeLocation,
        };
        return recipePack;
    }

    /// <summary>
    /// Sets the deployed name of a Radius resource type construct.
    /// </summary>
    /// <param name="resource">The Radius resource type construct.</param>
    /// <param name="name">The deployed resource name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusResourceTypeConstruct WithResourceName(
        this RadiusResourceTypeConstruct resource,
        string name)
    {
        resource.ResourceName = name;
        return resource;
    }

    /// <summary>
    /// Sets the recipe name of a Radius resource type construct.
    /// </summary>
    /// <param name="resource">The Radius resource type construct.</param>
    /// <param name="recipeName">The recipe name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusResourceTypeConstruct WithResourceRecipeName(
        this RadiusResourceTypeConstruct resource,
        string recipeName)
    {
        resource.RecipeName = recipeName;
        return resource;
    }

    /// <summary>
    /// Sets the application and environment references of a Radius resource construct.
    /// </summary>
    /// <param name="resource">The Radius resource type construct.</param>
    /// <param name="applicationIdentifier">The Bicep identifier of the application.</param>
    /// <param name="environmentIdentifier">The Bicep identifier of the environment.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusResourceTypeConstruct WithResourceScope(
        this RadiusResourceTypeConstruct resource,
        string applicationIdentifier,
        string environmentIdentifier)
    {
        resource.ApplicationId = ResourceIdExpression(applicationIdentifier);
        resource.EnvironmentId = ResourceIdExpression(environmentIdentifier);
        return resource;
    }

    /// <summary>
    /// Adds or replaces a string recipe parameter on a Radius resource construct.
    /// </summary>
    /// <param name="resource">The Radius resource type construct.</param>
    /// <param name="name">The recipe parameter name.</param>
    /// <param name="value">The recipe parameter value.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusResourceTypeConstruct WithStringRecipeParameter(
        this RadiusResourceTypeConstruct resource,
        string name,
        string value)
    {
        resource.RecipeParameters[name] = value;
        return resource;
    }

    /// <summary>
    /// Sets the image of a Radius container construct.
    /// </summary>
    /// <param name="container">The Radius container construct.</param>
    /// <param name="image">The container image reference.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport("radiusConstructWithContainerImage", MethodName = "withImage")]
    internal static RadiusContainerConstruct WithContainerImage(
        this RadiusContainerConstruct container,
        string image)
    {
        container.Image = image;
        return container;
    }

    /// <summary>
    /// Adds or replaces an environment variable on a Radius container construct.
    /// </summary>
    /// <param name="container">The Radius container construct.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">The environment variable value.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusContainerConstruct WithContainerEnvironmentVariable(
        this RadiusContainerConstruct container,
        string name,
        string value)
    {
        container.Env[name] = new ContainerEnvVarConstruct { Value = value };
        return container;
    }

    /// <summary>
    /// Adds or replaces a port on a Radius container construct.
    /// </summary>
    /// <param name="container">The Radius container construct.</param>
    /// <param name="name">The port name.</param>
    /// <param name="containerPort">The container port.</param>
    /// <param name="protocol">The transport protocol.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusContainerConstruct WithContainerPort(
        this RadiusContainerConstruct container,
        string name,
        int containerPort,
        string protocol)
    {
        container.Ports[name] = new ContainerPortConstruct
        {
            ContainerPort = containerPort,
            Protocol = protocol,
        };
        return container;
    }

    /// <summary>
    /// Adds or replaces a connection on a Radius container construct.
    /// </summary>
    /// <param name="container">The Radius container construct.</param>
    /// <param name="name">The connection name.</param>
    /// <param name="sourceResourceIdentifier">The Bicep identifier of the source resource.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusContainerConstruct WithContainerConnection(
        this RadiusContainerConstruct container,
        string name,
        string sourceResourceIdentifier)
    {
        container.Connections[name] = new ConnectionConstruct
        {
            Source = new IdentifierExpression($"{sourceResourceIdentifier}.id"),
        };
        return container;
    }

    /// <summary>
    /// Sets the application and environment references of a Radius container construct.
    /// </summary>
    /// <param name="container">The Radius container construct.</param>
    /// <param name="applicationIdentifier">The Bicep identifier of the application.</param>
    /// <param name="environmentIdentifier">The Bicep identifier of the environment.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static RadiusContainerConstruct WithContainerScope(
        this RadiusContainerConstruct container,
        string applicationIdentifier,
        string environmentIdentifier)
    {
        container.ApplicationId = ResourceIdExpression(applicationIdentifier);
        container.EnvironmentId = ResourceIdExpression(environmentIdentifier);
        return container;
    }

    /// <summary>
    /// Sets the deployed name and namespace of a legacy Radius environment construct.
    /// </summary>
    /// <param name="environment">The legacy Radius environment construct.</param>
    /// <param name="name">The deployed environment name.</param>
    /// <param name="kubernetesNamespace">The Kubernetes namespace.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static LegacyApplicationEnvironmentConstruct WithLegacyEnvironment(
        this LegacyApplicationEnvironmentConstruct environment,
        string name,
        string kubernetesNamespace)
    {
        environment.EnvironmentName = name;
        environment.ComputeNamespace = kubernetesNamespace;
        return environment;
    }

    /// <summary>
    /// Sets the Azure provider scope of a legacy Radius environment construct.
    /// </summary>
    /// <param name="environment">The legacy Radius environment construct.</param>
    /// <param name="scope">The Azure resource scope.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static LegacyApplicationEnvironmentConstruct WithLegacyAzureScope(
        this LegacyApplicationEnvironmentConstruct environment,
        string scope)
    {
        environment.AzureScope = scope;
        return environment;
    }

    /// <summary>
    /// Sets the AWS provider scope of a legacy Radius environment construct.
    /// </summary>
    /// <param name="environment">The legacy Radius environment construct.</param>
    /// <param name="scope">The AWS resource scope.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static LegacyApplicationEnvironmentConstruct WithLegacyAwsScope(
        this LegacyApplicationEnvironmentConstruct environment,
        string scope)
    {
        environment.AwsScope = scope;
        return environment;
    }

    /// <summary>
    /// Sets the deployed name of a legacy Radius application construct.
    /// </summary>
    /// <param name="application">The legacy Radius application construct.</param>
    /// <param name="name">The deployed application name.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static LegacyApplicationConstruct WithLegacyApplicationName(
        this LegacyApplicationConstruct application,
        string name)
    {
        application.ApplicationName = name;
        return application;
    }

    /// <summary>
    /// Sets the environment reference of a legacy Radius application construct.
    /// </summary>
    /// <param name="application">The legacy Radius application construct.</param>
    /// <param name="environmentIdentifier">The Bicep identifier of the legacy environment.</param>
    /// <returns>The same construct for chaining.</returns>
    [AspireExport]
    internal static LegacyApplicationConstruct WithLegacyApplicationEnvironment(
        this LegacyApplicationConstruct application,
        string environmentIdentifier)
    {
        application.EnvironmentId = ResourceIdExpression(environmentIdentifier);
        return application;
    }

    private static IdentifierExpression ResourceIdExpression(string bicepIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);
        return new IdentifierExpression($"{bicepIdentifier}.id");
    }
}

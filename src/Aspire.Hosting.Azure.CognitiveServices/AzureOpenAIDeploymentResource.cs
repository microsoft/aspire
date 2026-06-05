// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an Azure OpenAI Deployment resource.
/// </summary>
public class AzureOpenAIDeploymentResource : Resource, IResourceWithParent<AzureOpenAIResource>, IResourceWithConnectionString
{
    /// <value>"Standard"</value>
    private const string DefaultSkuName = "Standard";

    /// <value>8</value>
    private const int DefaultSkuCapacity = 8;

    private string _deploymentName;
    private string _modelName;
    private string _modelVersion;
    private string _skuName;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureOpenAIDeploymentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="modelName">The name of the model.</param>
    /// <param name="modelVersion">The version of the model.</param>
    /// <param name="parent">The parent Azure OpenAI resource.</param>
    public AzureOpenAIDeploymentResource(string name, string modelName, string modelVersion, AzureOpenAIResource parent)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        ArgumentException.ThrowIfNullOrEmpty(modelVersion);
        ArgumentNullException.ThrowIfNull(parent);

        _deploymentName = name;
        _modelName = modelName;
        _modelVersion = modelVersion;
        _skuName = DefaultSkuName;
        Parent = parent;
    }

    /// <summary>
    /// Gets or sets the name of the deployment.
    /// </summary>
    /// <remarks>
    /// This defaults to <see cref="Resource.Name"/>, but allows for a different deployment name in Azure.
    /// </remarks>
    public string DeploymentName
    {
        get => _deploymentName;
        set => _deploymentName = ThrowIfNullOrEmpty(value);
    }

    /// <summary>
    /// Gets the name of the model.
    /// </summary>
    public string ModelName
    {
        get => _modelName;
        set => _modelName = ThrowIfNullOrEmpty(value);
    }

    /// <summary>
    /// Gets the version of the model.
    /// </summary>
    public string ModelVersion
    {
        get => _modelVersion;
        set => _modelVersion = ThrowIfNullOrEmpty(value);
    }

    /// <summary>
    /// Gets the name of the SKU.
    /// </summary>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuName"/>.
    /// </value>
    public string SkuName
    {
        get => _skuName;
        set => _skuName = ThrowIfNullOrEmpty(value);
    }

    /// <summary>
    /// Gets the capacity of the SKU.
    /// </summary>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuCapacity"/>.
    /// </value>
    public int SkuCapacity { get; set; } = DefaultSkuCapacity;

    /// <summary>
    /// Gets the parent Azure OpenAI resource.
    /// </summary>
    public AzureOpenAIResource Parent { get; }

    /// <summary>
    /// Gets the connection string expression for the Azure OpenAI Deployment resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetConnectionString(DeploymentName);

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        foreach (var property in ((IResourceWithConnectionString)Parent).GetConnectionProperties())
        {
            yield return property;
        }

        yield return new("ModelName", ReferenceExpression.Create($"{DeploymentName}"));
    }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure AI Foundry Deployment.
/// </summary>
public class AzureAIFoundryDeploymentResource : Resource, IResourceWithParent<AzureAIFoundryResource>, IResourceWithConnectionString
{
    /// <value>"GlobalStandard"</value>
    private const string DefaultSkuName = "GlobalStandard";

    /// <value>1</value>
    private const int DefaultSkuCapacity = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAIFoundryDeploymentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the deployment.</param>
    /// <param name="modelName">The name of the model.</param>
    /// <param name="modelVersion">The version of the model.</param>
    /// <param name="format">The format of the model.</param>
    /// <param name="parent">The parent Azure AI Foundry resource.</param>
    public AzureAIFoundryDeploymentResource(string name, string modelName, string modelVersion, string format, AzureAIFoundryResource parent)
        : base(name)
    {
        DeploymentName = name;
        ModelName = modelName;
        ModelVersion = modelVersion;
        Format = format;
        Parent = parent;
    }

    /// <summary>
    /// This field is used to store the model id that is downloaded by Foundry Local based on the local machine's GPU.
    /// It is used in the connection string instead of <see cref="ModelName" />.
    /// </summary>
    internal string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the name of the deployment.
    /// </summary>
    /// <remarks>
    /// This defaults to <see cref="ModelName"/>, but allows for a different deployment name in Azure.
    /// When using Foundry Local, this is the model id.
    /// </remarks>
    public string DeploymentName { get; set; }

    /// <summary>
    /// Gets or sets the name of the model.
    /// </summary>
    public string ModelName { get; set; }

    /// <summary>
    /// Gets or sets the version of the model.
    /// </summary>
    public string ModelVersion { get; set; }

    /// <summary>
    /// Gets or sets the format of deployment model.
    /// </summary>
    /// <remarks>
    /// Typical values are "OpenAI", "Microsoft", "xAi", "Deepseek".
    /// </remarks> 
    public string Format { get; set; }

    /// <summary>
    /// Gets or sets the name of the SKU.
    /// </summary>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuName"/>.
    /// </value>
    public string SkuName { get; set; } = DefaultSkuName;

    /// <summary>
    /// Gets or sets the capacity of the SKU.
    /// </summary>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuCapacity"/>.
    /// </value>
    public int SkuCapacity { get; set; } = DefaultSkuCapacity;

    /// <summary>
    /// Gets the parent Azure AI Foundry resource.
    /// </summary>
    public AzureAIFoundryResource Parent { get; set; }

    /// <summary>
    /// Gets the connection string expression for the Azure AI Foundry resource with model/deployment information.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.IsEmulator
        ? ReferenceExpression.Create($"{Parent};Model={ModelId ?? ModelName}")
        : ReferenceExpression.Create($"{Parent};Deployment={DeploymentName}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        var model = Parent.IsEmulator ? ModelId ?? ModelName : DeploymentName;
        return Parent.CombineProperties([
            new("ModelName", ReferenceExpression.Create($"{model}")),
            new("Format", ReferenceExpression.Create($"{Format}")),
            new("Version", ReferenceExpression.Create($"{ModelVersion}")),
        ]);
    }
}

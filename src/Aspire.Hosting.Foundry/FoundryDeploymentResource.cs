// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Microsoft Foundry Deployment.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class FoundryDeploymentResource : Resource, IResourceWithParent<FoundryResource>, IResourceWithConnectionString
{
    /// <value>"GlobalStandard"</value>
    private const string DefaultSkuName = "GlobalStandard";

    /// <value>1</value>
    private const int DefaultSkuCapacity = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="FoundryDeploymentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the deployment.</param>
    /// <param name="modelName">The name of the model.</param>
    /// <param name="modelVersion">The version of the model.</param>
    /// <param name="format">The format of the model.</param>
    /// <param name="parent">The parent Microsoft Foundry resource.</param>
    public FoundryDeploymentResource(string name, string modelName, string modelVersion, string format, FoundryResource parent)
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
    /// Typical values are "OpenAI", "Microsoft", "Anthropic", "xAi", "Deepseek".
    /// </remarks> 
    public string Format { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific model metadata for this deployment.
    /// </summary>
    /// <remarks>
    /// Some model providers, such as Anthropic, require extra metadata when deploying a model.
    /// Configure these values by using <see cref="FoundryExtensions.WithProperties(IResourceBuilder{FoundryDeploymentResource}, Action{FoundryDeploymentResource})"/>.
    /// </remarks>
    public FoundryModelProviderData ModelProviderData { get; set; } = new();

    /// <summary>
    /// Gets or sets the name of the SKU.
    /// </summary>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuName"/>.
    /// </value>
    public string SkuName { get; set; } = DefaultSkuName;

    /// <summary>
    /// Gets or sets the capacity of the SKU, in thousands of tokens per minute (TPM).
    /// </summary>
    /// <remarks>
    /// For example, a value of 10 means 10,000 tokens per minute. Exceeding this rate
    /// limit results in HTTP 429 responses. See
    /// <a href="https://learn.microsoft.com/azure/ai-foundry/openai/how-to/quota">Azure AI quota management</a>
    /// for more information.
    /// </remarks>
    /// <value>
    /// The default value is <inheritdoc cref="DefaultSkuCapacity"/>.
    /// </value>
    public int SkuCapacity { get; set; } = DefaultSkuCapacity;

    /// <summary>
    /// Gets the parent Microsoft Foundry resource.
    /// </summary>
    public FoundryResource Parent { get; set; }

    internal bool RequiresModelProviderData =>
        string.Equals(Format, "Anthropic", StringComparison.OrdinalIgnoreCase);

    internal string[] GetMissingModelProviderDataFields()
    {
        List<string> missingFields = [];

        if (string.IsNullOrWhiteSpace(ModelProviderData.Industry))
        {
            missingFields.Add("industry");
        }

        if (string.IsNullOrWhiteSpace(ModelProviderData.OrganizationName))
        {
            missingFields.Add("organizationName");
        }

        if (string.IsNullOrWhiteSpace(ModelProviderData.CountryCode))
        {
            missingFields.Add("countryCode");
        }

        return [.. missingFields];
    }

    /// <summary>
    /// Gets the connection string expression for the Microsoft Foundry resource with model/deployment information.
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

/// <summary>
/// Represents provider-specific metadata for a Microsoft Foundry model deployment.
/// </summary>
/// <remarks>
/// Anthropic deployments currently require these values when they are provisioned in Azure.
/// </remarks>
public class FoundryModelProviderData
{
    /// <summary>
    /// Gets or sets the industry associated with the deployment.
    /// </summary>
    public string? Industry { get; set; }

    /// <summary>
    /// Gets or sets the organization name associated with the deployment.
    /// </summary>
    public string? OrganizationName { get; set; }

    /// <summary>
    /// Gets or sets the ISO country or region code associated with the deployment.
    /// </summary>
    public string? CountryCode { get; set; }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHub.Models;

/// <summary>
/// Represents a GitHub Model resource.
/// </summary>
public class GitHubModelResource : Resource, IResourceWithConnectionString
{
    internal ParameterResource DefaultKeyParameter { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubModelResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="model">The model name.</param>
    /// <param name="organization">The organization.</param>
    /// <param name="key">The key parameter.</param>
    public GitHubModelResource(string name, string model, ParameterResource? organization, ParameterResource key) : base(name)
    {
        Model = model;
        Organization = organization;
        Key = DefaultKeyParameter = key;
    }

    /// <summary>
    /// Gets or sets the model name, e.g., "openai/gpt-4o-mini".
    /// </summary>
    public string Model { get; set; }

    /// <summary>
    /// Gets or sets the organization login associated with the organization to which the request is to be attributed.
    /// </summary>
    /// <remarks>
    /// If set, the token must be attributed to an organization.
    /// </remarks>
    public ParameterResource? Organization { get; set; }

    /// <summary>
    /// Gets or sets the API key (PAT or GitHub App minted token) for accessing GitHub Models.
    /// </summary>
    /// <remarks>
    /// The token must have the <code>models: read</code> permission if using a fine-grained PAT or GitHub App minted token.
    /// </remarks>
    public ParameterResource Key { get; internal set; }

    /// <summary>
    /// Gets the connection string expression for the GitHub Models resource.
    /// </summary>
    private ReferenceExpression EndpointExpression
    {
        get
        {
            if (Organization is not null)
            {
                var builder = new ReferenceExpressionBuilder();
                builder.AppendLiteral("https://models.github.ai/orgs/");
                builder.Append($"{Organization:uri}");
                builder.AppendLiteral("/inference");

                return builder.Build();
            }

            return ReferenceExpression.Create($"https://models.github.ai/inference");
        }
    }

    /// <summary>
    /// Gets the connection string expression for the GitHub Models resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"Endpoint={EndpointExpression};Key={Key};Model={Model}");

    /// <summary>
    /// Gets the endpoint URI expression for the GitHub Models resource.
    /// </summary>
    /// <remarks>
    /// Format matches the configured endpoint, for example <c>https://models.github.ai/inference</c> or <c>https://models.github.ai/orgs/{organization}/inference</c> when an organization is specified.
    /// </remarks>
    public ReferenceExpression UriExpression => EndpointExpression;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", UriExpression);
        yield return new("Key", ReferenceExpression.Create($"{Key}"));
        yield return new("ModelName", ReferenceExpression.Create($"{Model}"));

        if (Organization is not null)
        {
            yield return new("Organization", ReferenceExpression.Create($"{Organization}"));
        }
    }
}

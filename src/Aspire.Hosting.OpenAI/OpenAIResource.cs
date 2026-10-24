// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenAI;

/// <summary>
/// Represents a logical OpenAI account/configuration that can host one or more <see cref="OpenAIModelResource"/> children.
/// </summary>
public sealed class OpenAIResource : Resource, IResourceWithConnectionString
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "OpenAI";

    internal ParameterResource DefaultKeyParameter { get; }
    private const string DefaultEndpoint = "https://api.openai.com/v1";

    /// <summary>
    /// Gets or sets the service endpoint base URI for OpenAI-compatible services.
    /// Defaults to https://api.openai.com/v1.
    /// </summary>
    public string Endpoint { get; internal set; } = DefaultEndpoint;

    /// <summary>
    /// Creates a new <see cref="OpenAIResource"/>.
    /// </summary>
    /// <param name="name">Resource name.</param>
    /// <param name="key">API key parameter for OpenAI.</param>
    public OpenAIResource(string name, ParameterResource key) : base(name)
    {
        Key = DefaultKeyParameter = key;
    }

    /// <summary>
    /// Gets or sets the API key for accessing OpenAI.
    /// </summary>
    public ParameterResource Key { get; internal set; }

    /// <summary>
    /// Gets the base connection string for the OpenAI account.
    /// </summary>
    private ReferenceExpression EndpointExpression => ReferenceExpression.Create($"{Endpoint}");

    /// <summary>
    /// Gets the base connection string for the OpenAI account.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"Endpoint={EndpointExpression};Key={Key}");

    /// <summary>
    /// Gets the endpoint URI expression for the OpenAI account.
    /// </summary>
    /// <remarks>
    /// Format matches the configured endpoint, for example <c>https://api.openai.com/v1</c>.
    /// </remarks>
    public ReferenceExpression UriExpression => EndpointExpression;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Endpoint", EndpointExpression);
        yield return new("Uri", UriExpression);
        yield return new("Key", ReferenceExpression.Create($"{Key}"));
    }
}

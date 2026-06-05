// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenAI;

/// <summary>
/// Represents an OpenAI Model resource.
/// </summary>
public class OpenAIModelResource : Resource, IResourceWithParent<OpenAIResource>, IResourceWithConnectionString
{
    /// <summary>
    /// Parent OpenAI account resource from which settings (like API key) can be shared.
    /// </summary>
    public OpenAIResource Parent { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIModelResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="model">The model name.</param>
    /// <param name="parent">The parent OpenAI resource.</param>
    /// <remarks>The API key is owned by the parent <see cref="OpenAIResource"/>.</remarks>
    public OpenAIModelResource(string name, string model, OpenAIResource parent) : base(name)
    {
        Model = model;
        Parent = parent;
        // API key is owned by the parent; model will always compose from the parent
    }

    /// <summary>
    /// Gets or sets the model name, e.g., "gpt-4o-mini".
    /// </summary>
    public string Model { get; set; }

    /// <summary>
    /// Gets the connection string expression for the OpenAI Model resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{Parent};Model={Model}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("ModelName", ReferenceExpression.Create($"{Model}")),
        ]);
}

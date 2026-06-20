// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Ats;

/// <summary>
/// Options for customizing parameter inputs from polyglot app hosts.
/// </summary>
[AspireDto]
internal sealed class ParameterCustomInputOptions
{
    /// <summary>
    /// Gets or sets the type of the input.
    /// </summary>
    public InputType? InputType { get; set; }

    /// <summary>
    /// Gets or sets the label for the input.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the description for the input.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether the description should be rendered as Markdown.
    /// </summary>
    public bool? EnableDescriptionMarkdown { get; set; }

    /// <summary>
    /// Gets or sets the choice options keyed by submitted value.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; set; }

    /// <summary>
    /// Gets or sets the initial value of the input.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Gets or sets the placeholder text for the input.
    /// </summary>
    public string? Placeholder { get; set; }

    /// <summary>
    /// Gets or sets whether custom choices are allowed.
    /// </summary>
    public bool? AllowCustomChoice { get; set; }

    /// <summary>
    /// Gets or sets whether the input is disabled.
    /// </summary>
    public bool? Disabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum length for text inputs.
    /// </summary>
    public int? MaxLength { get; set; }
}

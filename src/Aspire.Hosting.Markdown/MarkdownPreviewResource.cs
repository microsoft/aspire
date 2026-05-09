// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Markdown file that can be viewed from the dashboard.
/// </summary>
public sealed class MarkdownPreviewResource : Resource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownPreviewResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="path">The full path to the Markdown file.</param>
    public MarkdownPreviewResource(string name, string path) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = path;
    }

    /// <summary>
    /// Gets the full path to the Markdown file.
    /// </summary>
    public string Path { get; }
}


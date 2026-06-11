// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents pipeline summary information to be displayed after pipeline completion.
/// This is a general-purpose key-value collection that pipeline steps can contribute to.
/// </summary>
/// <remarks>
/// <para>
/// This class provides a flexible way for any pipeline step to contribute
/// information to be displayed after pipeline execution. The data is stored as
/// key-value pairs that will be formatted as a table or list in the CLI output.
/// </para>
/// <para>
/// Pipeline steps can add any relevant information such as resource group names,
/// subscription IDs, URLs, namespaces, cluster names, or any other details.
/// Values can be plain text or Markdown-formatted by using <see cref="MarkdownString"/>.
/// </para>
/// <para>
/// The summary is available via the <see cref="Aspire.Hosting.Pipelines.PipelineContext.Summary"/>
/// property and can be accessed from any pipeline step.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // In a pipeline step, add to the summary
/// public async Task ExecuteAsync(PipelineStepContext context)
/// {
///     // Do work...
///
///     // Add plain text summary items
///     context.PipelineContext.Summary.Add("☁️ Target", "Azure");
///     context.PipelineContext.Summary.Add("📦 Resource Group", "rg-myapp");
///     context.PipelineContext.Summary.Add("🔑 Subscription", "12345678-1234-1234-1234-123456789012");
///     context.PipelineContext.Summary.Add("🌐 Location", "eastus");
/// }
///
///     // Kubernetes example
///     context.PipelineContext.Summary.Add("☸️ Target", "Kubernetes");
///     context.PipelineContext.Summary.Add("📦 Namespace", "production");
///     context.PipelineContext.Summary.Add("🖥️ Cluster", "my-cluster");
///
///     // Docker example
///     context.PipelineContext.Summary.Add("🐳 Target", "Docker");
///     context.PipelineContext.Summary.Add("🌐 Endpoint", "localhost:8080");
///
///     // Add a Markdown-formatted value (e.g., a clickable link)
///     context.PipelineContext.Summary.Add("📦 Resource Group", new MarkdownString($"[{rgName}]({portalUrl})"));
/// </code>
/// </example>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
[AspireExport(ExposeMethods = true)]
public sealed class PipelineSummary
{
    private readonly object _lock = new();
    private readonly List<PipelineSummaryItem> _items = [];

    /// <summary>
    /// Gets the items in the pipeline summary as a read-only collection.
    /// Items are displayed in the order they were added.
    /// </summary>
    public ReadOnlyCollection<PipelineSummaryItem> Items
    {
        get
        {
            lock (_lock)
            {
                return new ReadOnlyCollection<PipelineSummaryItem>(_items.ToList());
            }
        }
    }

    /// <summary>
    /// Adds a key-value pair to the pipeline summary with a plain-text value.
    /// </summary>
    /// <param name="key">The key or label for the item (e.g., "Namespace", "URL").</param>
    /// <param name="value">The plain-text value for the item.</param>
    public void Add(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        lock (_lock)
        {
            _items.Add(new PipelineSummaryItem(key, value, enableMarkdown: false));
        }
    }

    /// <summary>
    /// Adds a key-value pair to the pipeline summary with a Markdown-formatted value.
    /// </summary>
    /// <param name="key">The key or label for the item (e.g., "Namespace", "URL").</param>
    /// <param name="value">The Markdown-formatted value for the item.</param>
    [AspireExportIgnore(Reason = "MarkdownString is not exported to ATS. Use addMarkdown instead.")]
    public void Add(string key, MarkdownString value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            _items.Add(new PipelineSummaryItem(key, value.Value, enableMarkdown: true));
        }
    }

}

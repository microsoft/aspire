// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Markdown preview resources to an application.
/// </summary>
public static class MarkdownPreviewResourceBuilderExtensions
{
    private const string MarkdownPreviewResourceType = "MarkdownPreview";
    private const string ViewMarkdownCommandName = "markdown-preview-view";

    /// <summary>
    /// Adds a Markdown file to the application model so it can be viewed from the dashboard.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="path">The path to the Markdown file. Relative paths are resolved from the AppHost directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a Markdown preview resource")]
    public static IResourceBuilder<MarkdownPreviewResource> AddMarkdownPreview(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path, builder.AppHostDirectory);
        var sourcePath = Path.GetRelativePath(builder.AppHostDirectory, fullPath);
        var resource = new MarkdownPreviewResource(name, fullPath);

        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = MarkdownPreviewResourceType,
                State = KnownResourceStates.Running,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, sourcePath),
                    new("Path", fullPath)
                ],
            })
            .WithIconName("DocumentBulletList")
            .WithCommand(ViewMarkdownCommandName, "View markdown", ExecuteViewMarkdownCommandAsync, new CommandOptions
            {
                Description = "Opens the Markdown file.",
                IconName = "DocumentBulletList",
                IsHighlighted = true
            });

        async Task<ExecuteCommandResult> ExecuteViewMarkdownCommandAsync(ExecuteCommandContext context)
        {
            var path = resource.Path;

            if (Directory.Exists(path))
            {
                return CommandResults.Failure(string.Format(CultureInfo.InvariantCulture, "Markdown path '{0}' is a directory.", path));
            }

            if (!File.Exists(path))
            {
                return CommandResults.Failure(string.Format(CultureInfo.InvariantCulture, "Markdown file '{0}' does not exist.", path));
            }

            try
            {
                var markdown = await File.ReadAllTextAsync(path, context.CancellationToken).ConfigureAwait(false);
                return CommandResults.Success(string.Format(CultureInfo.InvariantCulture, "Opened Markdown file '{0}'.", path), markdown, CommandResultFormat.Markdown, displayImmediately: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResults.Failure(ex.Message);
            }
            catch (IOException ex)
            {
                return CommandResults.Failure(ex.Message);
            }
        }
    }
}


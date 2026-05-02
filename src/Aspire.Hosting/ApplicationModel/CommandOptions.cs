// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to describe dashboard command arguments.

/// <summary>
/// Optional configuration for resource commands added with <see cref="ResourceBuilderExtensions.WithCommand{T}(Aspire.Hosting.ApplicationModel.IResourceBuilder{T}, string, string, Func{Aspire.Hosting.ApplicationModel.ExecuteCommandContext, Task{Aspire.Hosting.ApplicationModel.ExecuteCommandResult}}, Aspire.Hosting.ApplicationModel.CommandOptions?)"/>.
/// </summary>
[AspireDto]
public class CommandOptions
{
    internal static CommandOptions Default { get; } = new();

    /// <summary>
    /// Optional description of the command, to be shown in the UI.
    /// Could be used as a tooltip. May be localized.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Obsolete optional parameter that configures the command in some way.
    /// Clients must return any value provided by the server when invoking the command.
    /// </summary>
    [Obsolete("Use ArgumentInputs to describe invocation arguments and ExecuteCommandContext.Arguments to read them.")]
    public object? Parameter { get; set; }

    /// <summary>
    /// Gets or sets the inputs used to describe the invocation arguments accepted by the command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each input name maps to a property in the JSON object supplied to <see cref="ExecuteCommandContext.Arguments"/>.
    /// Dashboard clients can render these inputs before invoking the command, while non-interactive clients can use the
    /// metadata to construct the same JSON object directly.
    /// </para>
    /// </remarks>
    public IReadOnlyList<InteractionInput>? ArgumentInputs { get; set; }

    /// <summary>
    /// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog
    /// and the confirmation message before starting the command.
    /// </summary>
    public string? ConfirmationMessage { get; set; }

    /// <summary>
    /// The icon name for the command. The name should be a valid FluentUI icon name from <see href="https://aka.ms/fluentui-system-icons"/>.
    /// </summary>
    public string? IconName { get; set; }

    /// <summary>
    /// The icon variant.
    /// </summary>
    public IconVariant? IconVariant { get; set; }

    /// <summary>
    /// A flag indicating whether the command is highlighted in the UI.
    /// </summary>
    public bool IsHighlighted { get; set; }

    /// <summary>
    /// <para>A callback that is used to update the command state. The callback is executed when the command's resource snapshot is updated.</para>
    /// <para>If a callback isn't specified, the command is always enabled.</para>
    /// </summary>
    public Func<UpdateCommandStateContext, ResourceCommandState>? UpdateState { get; set; }
}

#pragma warning restore ASPIREINTERACTION001

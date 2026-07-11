// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Api;

internal sealed record DeckConfig(
    string? ApplicationName,
    string? ResourceServiceUrl,
    string? OtlpGrpcUrl,
    string? OtlpHttpUrl,
    string Version);

internal sealed record DeckResource(
    string Name,
    string ResourceType,
    string DisplayName,
    string Uid,
    string? State,
    string? StateStyle,
    string? Health,
    DateTime? CreatedAt,
    DateTime? StartedAt,
    DateTime? StoppedAt,
    DeckResourceUrl[] Urls,
    DeckResourceProperty[] Properties,
    DeckEnvironmentVariable[] Environment,
    DeckHealthReport[] HealthReports,
    DeckResourceCommand[] Commands,
    DeckResourceRelationship[] Relationships,
    bool IsHidden,
    bool SupportsDetailedTelemetry,
    string? IconName,
    string? IconVariant);

internal sealed record DeckResourceUrl(
    string? Name,
    string Url,
    bool IsInternal,
    bool IsInactive,
    string? DisplayName,
    int SortOrder);

internal sealed record DeckResourceProperty(
    string Name,
    string? DisplayName,
    string Value,
    bool IsSensitive,
    bool IsHighlighted,
    int? SortOrder);

internal sealed record DeckEnvironmentVariable(
    string Name,
    string? Value,
    bool IsFromSpec);

internal sealed record DeckHealthReport(
    string? Status,
    string Key,
    string Description);

internal sealed record DeckResourceCommand(
    string Name,
    string DisplayName,
    string? DisplayDescription,
    string? ConfirmationMessage,
    string? IconName,
    string IconVariant,
    bool IsHighlighted,
    string State);

internal sealed record DeckResourceRelationship(
    string ResourceName,
    string Type);

internal sealed record DeckExecuteCommandRequest(
    string ResourceName,
    string CommandName);

internal sealed record DeckCommandResponse(
    string Kind,
    string? Message);

internal sealed record DeckInteraction(
    int InteractionId,
    string Kind,
    string Title,
    string Message,
    string PrimaryButtonText,
    string SecondaryButtonText,
    bool ShowSecondaryButton,
    bool ShowDismiss,
    bool EnableMessageMarkdown,
    string Intent,
    DeckInteractionInput[] Inputs,
    string LinkText,
    string LinkUrl);

internal sealed record DeckInteractionInput(
    string Name,
    string Label,
    string Placeholder,
    string InputType,
    bool Required,
    string[][] Options,
    string Value,
    string[] ValidationErrors,
    string Description,
    bool EnableDescriptionMarkdown,
    int MaxLength,
    bool AllowCustomChoice,
    bool Disabled,
    bool UpdateStateOnChange);

internal sealed record DeckRespondInteractionRequest(
    int InteractionId,
    string Action,
    Dictionary<string, string>? Values);

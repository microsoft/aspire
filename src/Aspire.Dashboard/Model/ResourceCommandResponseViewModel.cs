// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Model;

public class ResourceCommandResponseViewModel
{
    public required ResourceCommandResponseKind Kind { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Message { get; init; }
    public ResourceCommandResultViewModel? Result { get; init; }
    public ImmutableArray<InteractionInput> ArgumentInputs { get; init; } = [];
}

/// <summary>
/// Represents a value produced by a command.
/// </summary>
public class ResourceCommandResultViewModel
{
    public required string Value { get; init; }
    public CommandResultFormat Format { get; init; }
    public bool DisplayImmediately { get; init; }
}

// Must be kept in sync with ResourceCommandResponseKind in the resource_service.proto file
public enum ResourceCommandResponseKind
{
    Undefined = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    ValidationFailed = 4
}

/// <summary>
/// Specifies the format of a command result.
/// </summary>
public enum CommandResultFormat
{
    /// <summary>
    /// Plain text result.
    /// </summary>
    Text,

    /// <summary>
    /// JSON result.
    /// </summary>
    Json,

    /// <summary>
    /// Markdown result.
    /// </summary>
    Markdown
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public class ResourceCommandResponseViewModel
{
    public required ResourceCommandResponseKind Kind { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Result { get; init; }
    public CommandResultFormat ResultFormat { get; init; }
}

public enum CommandResultFormat
{
    None = 0,
    Text = 1,
    Json = 2
}

// Must be kept in sync with ResourceCommandResponseKind in the resource_service.proto file
public enum ResourceCommandResponseKind
{
    Undefined = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Represents an error that occurred during ATS capability invocation.
/// </summary>
internal sealed class AtsError
{
    /// <summary>
    /// Gets or sets the machine-readable error code.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Gets or sets the human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Gets or sets the capability ID that failed, if applicable.
    /// </summary>
    [JsonPropertyName("capability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Capability { get; init; }

    /// <summary>
    /// Gets or sets additional error details.
    /// </summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AtsErrorDetails? Details { get; init; }

    /// <summary>
    /// Converts this error to a JSON object.
    /// </summary>
    public JsonObject ToJsonObject()
    {
        var obj = new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message
        };

        if (Capability is not null)
        {
            obj["capability"] = Capability;
        }

        if (Details is not null)
        {
            obj["details"] = Details.ToJsonObject();
        }

        return obj;
    }
}

/// <summary>
/// Additional details about an ATS error.
/// </summary>
internal sealed class AtsErrorDetails
{
    /// <summary>
    /// Gets or sets the parameter that had the issue.
    /// </summary>
    [JsonPropertyName("parameter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Parameter { get; init; }

    /// <summary>
    /// Gets or sets the expected type or value.
    /// </summary>
    [JsonPropertyName("expected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; init; }

    /// <summary>
    /// Gets or sets the actual type or value.
    /// </summary>
    [JsonPropertyName("actual")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actual { get; init; }

    /// <summary>
    /// Converts this details object to a JSON object.
    /// </summary>
    public JsonObject ToJsonObject()
    {
        var obj = new JsonObject();

        if (Parameter is not null)
        {
            obj["parameter"] = Parameter;
        }

        if (Expected is not null)
        {
            obj["expected"] = Expected;
        }

        if (Actual is not null)
        {
            obj["actual"] = Actual;
        }

        return obj;
    }
}

/// <summary>
/// Standard ATS error codes.
/// </summary>
internal static class AtsErrorCodes
{
    /// <summary>
    /// The specified capability ID was not found.
    /// </summary>
    public const string CapabilityNotFound = "CAPABILITY_NOT_FOUND";

    /// <summary>
    /// The specified handle ID was not found or has been disposed.
    /// </summary>
    public const string HandleNotFound = "HANDLE_NOT_FOUND";

    /// <summary>
    /// The handle type doesn't satisfy the capability's type constraint.
    /// </summary>
    public const string TypeMismatch = "TYPE_MISMATCH";

    /// <summary>
    /// A required argument is missing or has the wrong type.
    /// </summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>
    /// An argument value is outside the valid range.
    /// </summary>
    public const string ArgumentOutOfRange = "ARGUMENT_OUT_OF_RANGE";

    /// <summary>
    /// An error occurred during callback invocation.
    /// </summary>
    public const string CallbackError = "CALLBACK_ERROR";

    /// <summary>
    /// An unexpected error occurred during capability execution.
    /// </summary>
    public const string InternalError = "INTERNAL_ERROR";
}

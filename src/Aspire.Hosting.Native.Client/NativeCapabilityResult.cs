// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Native.Client;

/// <summary>Validates ATS result failures before generated result deserialization.</summary>
internal static class NativeCapabilityResult
{
    public static JsonElement Parse(JsonElement result)
    {
        // ATS failures are successful JSON-RPC results:
        // {"result":{"$error":{"code":"HANDLE_NOT_FOUND","message":"..."}}}.
        // Missing, null or non-string failure fields are protocol errors, not
        // capability failures, and must never expose their raw payload in diagnostics.
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("$error", out var error))
        {
            var failure = ParseFailure(result, error);
            throw new NativeCapabilityException(failure.Code, failure.Message);
        }

        return result;
    }

    private static CapabilityFailure ParseFailure(JsonElement result, JsonElement error)
    {
        if (result.EnumerateObject().Count() != 1 || error.ValueKind != JsonValueKind.Object ||
            error.EnumerateObject().Count() != 2 ||
            !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
            code.GetString() is not { Length: > 0 } codeValue ||
            !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            message.GetString() is not { } messageValue)
        {
            throw new InvalidDataException("The native capability failure response is malformed.");
        }

        return new CapabilityFailure(codeValue, messageValue);
    }

    private readonly record struct CapabilityFailure(string Code, string Message);
}

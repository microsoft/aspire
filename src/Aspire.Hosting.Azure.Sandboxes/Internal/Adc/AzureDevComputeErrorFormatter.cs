// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Azure;

// Builds a diagnostic message from an ADC error response without leaking secrets.
//
// Threat model: ADC error bodies are untrusted text. Validation failures in particular can echo
// values from the request Aspire sent, and sandbox create requests carry resolved secret
// environment variable values. Any exception message we build ends up in pipeline logs and CLI
// output, so free-form service text must never be surfaced verbatim.
//
// ADC returns RFC 9457 problem details (https://www.rfc-editor.org/rfc/rfc9457) using the
// ASP.NET Core shape, for example:
//
//   {
//     "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
//     "title": "InvalidResourceTier",
//     "status": 400,
//     "detail": "Disk size '20480Mi' exceeds tier maximum ...",
//     "errorCode": 18,
//     "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
//     "requestId": "8c1f3f0e-2a8b-4f5e-9c43-2b7d1f6c1a90"
//   }
//
// Validation failures use the same shape with a free-form "title" (for example
// "One or more validation errors occurred.") and an "errors" object mapping request member names
// to messages that may quote the rejected value.
//
// Only fields that are both structurally constrained and useful for support are surfaced:
//   - "status" and a numeric "errorCode" are integers and cannot carry text.
//   - "title" and a string "errorCode" are surfaced only when they are short identifier-like tokens
//     (e.g. "InvalidResourceTier"); sentences, punctuation, and whitespace are rejected.
//   - "traceId" and "requestId" are surfaced only when they match a correlation-ID character set.
// Every surfaced string is additionally dropped when it overlaps any string value Aspire sent in the
// request body, so an identifier-shaped secret echoed back by the service is still redacted.
// "detail", "errors", "type", "instance", and any other members stay redacted: scrubbing every
// sensitive value out of arbitrary prose cannot be done reliably (the service may re-encode,
// truncate, or quote values), so we do not attempt it.
internal static partial class AzureDevComputeErrorFormatter
{
    internal const int MaxErrorBodyBytes = 64 * 1024;

    internal const string RedactedMessage = "The service returned an error response whose details were redacted.";
    internal const string AdditionalDetailsRedactedMessage = "Additional service details were redacted.";

    // Request values shorter than this are too common (e.g. "1", "true", "2000m") to treat a candidate
    // that merely contains them as an echo. Candidates that are contained in any request value are
    // always dropped regardless of length.
    private const int MinRequestValueLengthForContainmentCheck = 8;

    public static async Task<string> GetErrorMessageAsync(HttpResponseMessage response, object? requestContent, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content.Headers.ContentLength == 0)
        {
            return string.Empty;
        }

        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return RedactedMessage;
        }

        if (body is null)
        {
            // The body exceeded the read limit. Don't try to parse a truncated document.
            return RedactedMessage;
        }

        if (body.Length == 0)
        {
            return string.Empty;
        }

        return Format(body, (int)response.StatusCode, GetRequestStringValues(requestContent, serializerOptions));
    }

    internal static string Format(byte[] body, int httpStatusCode, IReadOnlyCollection<string> requestStringValues)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return IsWhiteSpace(body) ? string.Empty : RedactedMessage;
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return RedactedMessage;
            }

            string? title = null;
            string? errorCode = null;
            int? status = null;
            string? traceId = null;
            string? requestId = null;
            var hasRedactedContent = false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var surfaced = property.Name.ToLowerInvariant() switch
                {
                    "title" when title is null => TrySetIdentifier(property.Value, requestStringValues, ref title),
                    "errorcode" when errorCode is null => TrySetErrorCode(property.Value, requestStringValues, ref errorCode),
                    "status" when status is null => TrySetStatus(property.Value, ref status),
                    "traceid" when traceId is null => TrySetCorrelationId(property.Value, requestStringValues, ref traceId),
                    "requestid" when requestId is null => TrySetCorrelationId(property.Value, requestStringValues, ref requestId),
                    _ => false
                };

                hasRedactedContent |= !surfaced;
            }

            var parts = new List<string>();

            var codes = new List<string>();
            if (errorCode is not null)
            {
                codes.Add($"errorCode {errorCode}");
            }

            // The HTTP status is already part of the exception message, so only repeat the
            // problem-details status when it disagrees with the transport status.
            if (status is not null && status != httpStatusCode)
            {
                codes.Add(string.Create(CultureInfo.InvariantCulture, $"status {status}"));
            }

            if (title is not null)
            {
                parts.Add(codes.Count > 0 ? $"{title} ({string.Join(", ", codes)})." : $"{title}.");
            }
            else if (codes.Count > 0)
            {
                parts.Add($"Service {string.Join(", ", codes)}.");
            }

            var ids = new List<string>();
            if (traceId is not null)
            {
                ids.Add($"traceId={traceId}");
            }

            if (requestId is not null)
            {
                ids.Add($"requestId={requestId}");
            }

            if (ids.Count > 0)
            {
                parts.Add($"{string.Join(", ", ids)}.");
            }

            if (parts.Count == 0)
            {
                return RedactedMessage;
            }

            if (hasRedactedContent)
            {
                parts.Add(AdditionalDetailsRedactedMessage);
            }

            return string.Join(" ", parts);
        }
    }

    internal static IReadOnlyCollection<string> GetRequestStringValues(object? requestContent, JsonSerializerOptions serializerOptions)
    {
        if (requestContent is null)
        {
            return [];
        }

        // Serialize the same way SendCoreAsync does so we compare against the exact values put on the wire.
        var element = JsonSerializer.SerializeToElement(requestContent, requestContent.GetType(), serializerOptions);
        var values = new HashSet<string>(StringComparer.Ordinal);
        CollectStringValues(element, values);
        return values;
    }

    private static void CollectStringValues(JsonElement element, HashSet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { Length: > 0 } value)
                {
                    values.Add(value);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStringValues(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStringValues(item, values);
                }
                break;
        }
    }

    private static bool TrySetIdentifier(JsonElement value, IReadOnlyCollection<string> requestStringValues, ref string? target)
    {
        if (value.ValueKind is JsonValueKind.String &&
            value.GetString() is { } text &&
            IdentifierRegex().IsMatch(text) &&
            !OverlapsRequestValue(text, requestStringValues))
        {
            target = text;
            return true;
        }

        return false;
    }

    private static bool TrySetErrorCode(JsonElement value, IReadOnlyCollection<string> requestStringValues, ref string? target)
    {
        if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var code))
        {
            target = code.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return TrySetIdentifier(value, requestStringValues, ref target);
    }

    private static bool TrySetStatus(JsonElement value, ref int? target)
    {
        if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var status) && status is >= 100 and <= 599)
        {
            target = status;
            return true;
        }

        return false;
    }

    private static bool TrySetCorrelationId(JsonElement value, IReadOnlyCollection<string> requestStringValues, ref string? target)
    {
        if (value.ValueKind is JsonValueKind.String &&
            value.GetString() is { } text &&
            CorrelationIdRegex().IsMatch(text) &&
            !OverlapsRequestValue(text, requestStringValues))
        {
            target = text;
            return true;
        }

        return false;
    }

    private static bool OverlapsRequestValue(string candidate, IReadOnlyCollection<string> requestStringValues)
    {
        foreach (var requestValue in requestStringValues)
        {
            if (requestValue.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
                (requestValue.Length >= MinRequestValueLengthForContainmentCheck &&
                 candidate.Contains(requestValue, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsWhiteSpace(byte[] body)
    {
        return string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(body));
    }

    // A short identifier token such as "InvalidResourceTier" or "Sandbox.QuotaExceeded".
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    // W3C trace IDs ("00-<32 hex>-<16 hex>-01"), GUIDs, and similar opaque correlation IDs.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationIdRegex();
}

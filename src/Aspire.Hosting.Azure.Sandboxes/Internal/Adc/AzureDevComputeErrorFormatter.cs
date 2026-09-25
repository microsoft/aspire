// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
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
//   - "status" and a numeric "errorCode" are integers and cannot carry free-form text.
//   - "title" and a string "errorCode" are surfaced only when they are short identifier-like tokens
//     (e.g. "InvalidResourceTier"); sentences, punctuation, and whitespace are rejected.
//   - "traceId" and "requestId" are surfaced only when they match a correlation-ID character set.
// Every surfaced value, numeric or string, is additionally dropped when it overlaps any string value
// Aspire sent in the request body, so an identifier-shaped or digit-only secret echoed back by the
// service is still redacted.
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

    private const string TitlePropertyName = "title";
    private const string ErrorCodePropertyName = "errorCode";
    private const string StatusPropertyName = "status";
    private const string TraceIdPropertyName = "traceId";
    private const string RequestIdPropertyName = "requestId";

    public static async Task<string> GetErrorMessageAsync(HttpResponseMessage response, object? requestContent, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();

        if (response.Content.Headers.ContentLength == 0)
        {
            return string.Empty;
        }

        if (response.Content.Headers.ContentLength > MaxErrorBodyBytes)
        {
            return RedactedMessage;
        }

        // Read one byte past the limit so an oversized body can be detected without reading the
        // rest of it. Don't try to parse a truncated document.
        var buffer = ArrayPool<byte>.Shared.Rent(MaxErrorBodyBytes + 1);
        try
        {
            int bytesRead;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                bytesRead = await stream.ReadAtLeastAsync(buffer.AsMemory(0, MaxErrorBodyBytes + 1), MaxErrorBodyBytes + 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return RedactedMessage;
            }

            if (bytesRead > MaxErrorBodyBytes)
            {
                return RedactedMessage;
            }

            return Format(buffer.AsMemory(0, bytesRead), (int)response.StatusCode, GetRequestStringValues(requestContent, serializerOptions));
        }
        finally
        {
            // The body may echo request secrets, so clear it before handing the buffer to other pool users.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static string Format(ReadOnlyMemory<byte> body, int httpStatusCode, IReadOnlyCollection<string> requestStringValues)
    {
        // JSON insignificant whitespace is limited to ASCII space, tab, CR, and LF:
        // https://www.rfc-editor.org/rfc/rfc8259#section-2
        if (body.Span[Ascii.Trim(body.Span)].IsEmpty)
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return RedactedMessage;
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
                bool surfaced;
                try
                {
                    surfaced = property.Name switch
                    {
                        var name when IsProperty(name, TitlePropertyName) && title is null => TrySetIdentifier(property.Value, requestStringValues, ref title),
                        var name when IsProperty(name, ErrorCodePropertyName) && errorCode is null => TrySetErrorCode(property.Value, requestStringValues, ref errorCode),
                        var name when IsProperty(name, StatusPropertyName) && status is null => TrySetStatus(property.Value, requestStringValues, ref status),
                        var name when IsProperty(name, TraceIdPropertyName) && traceId is null => TrySetCorrelationId(property.Value, requestStringValues, ref traceId),
                        var name when IsProperty(name, RequestIdPropertyName) && requestId is null => TrySetCorrelationId(property.Value, requestStringValues, ref requestId),
                        _ => false
                    };
                }
                catch (InvalidOperationException)
                {
                    // JsonDocument.Parse accepts escaped lone surrogates such as {"title":"\uD800"},
                    // but decoding that name or value to a .NET string throws. Treat the member as
                    // redacted content rather than letting the exception replace the HTTP failure.
                    surfaced = false;
                }

                hasRedactedContent |= !surfaced;
            }

            var parts = new List<string>();

            var codes = new List<string>();
            if (errorCode is not null)
            {
                codes.Add($"{ErrorCodePropertyName} {errorCode}");
            }

            // The HTTP status is already part of the exception message, so only repeat the
            // problem-details status when it disagrees with the transport status.
            if (status is not null && status != httpStatusCode)
            {
                codes.Add(string.Create(CultureInfo.InvariantCulture, $"{StatusPropertyName} {status}"));
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
                ids.Add($"{TraceIdPropertyName}={traceId}");
            }

            if (requestId is not null)
            {
                ids.Add($"{RequestIdPropertyName}={requestId}");
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
        if (value.ValueKind is JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var code) &&
                code.ToString(CultureInfo.InvariantCulture) is var text &&
                !OverlapsRequestValueAsNumber(text, requestStringValues))
            {
                target = text;
                return true;
            }

            return false;
        }

        return TrySetIdentifier(value, requestStringValues, ref target);
    }

    private static bool TrySetStatus(JsonElement value, IReadOnlyCollection<string> requestStringValues, ref int? target)
    {
        if (value.ValueKind is JsonValueKind.Number &&
            value.TryGetInt32(out var status) &&
            status is >= 100 and <= 599 &&
            !OverlapsRequestValueAsNumber(status.ToString(CultureInfo.InvariantCulture), requestStringValues))
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

    // Request secrets are always sent as JSON strings, but they can be digit-only (for example a PIN
    // or numeric API key), and the service may echo one back as a JSON number such as
    // { "errorCode": 1234567890 }. Short numbers such as errorCode 18 or status 400 routinely appear
    // inside unrelated request values ("20480Mi", image digests, GUIDs), so containment is only
    // checked once the number is long enough to be distinctive; an exact match is always rejected.
    private static bool OverlapsRequestValueAsNumber(string candidate, IReadOnlyCollection<string> requestStringValues)
    {
        foreach (var requestValue in requestStringValues)
        {
            if (string.Equals(requestValue, candidate, StringComparison.Ordinal) ||
                (candidate.Length >= MinRequestValueLengthForContainmentCheck && requestValue.Contains(candidate, StringComparison.Ordinal)) ||
                (requestValue.Length >= MinRequestValueLengthForContainmentCheck && candidate.Contains(requestValue, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProperty(string name, string propertyName) =>
        string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase);

    // A short identifier token such as "InvalidResourceTier" or "Sandbox.QuotaExceeded".
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    // W3C trace IDs ("00-<32 hex>-<16 hex>-01"), GUIDs, and similar opaque correlation IDs.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationIdRegex();
}

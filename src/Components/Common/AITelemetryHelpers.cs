// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire;

internal static class TelemetryHelpers
{
    /// <summary>Gets a value indicating whether the OpenTelemetry clients should enable their EnableSensitiveData property's by default.</summary>
    /// <remarks>
    /// Defaults to false. May be overridden by setting the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT environment variable to "true".
    /// Mirrors Microsoft.Extensions.AI default: https://github.com/dotnet/extensions/blob/c4e57fb1e6b8403a527ea3cd737f1146dcbc1f31/src/Libraries/Microsoft.Extensions.AI/TelemetryHelpers.cs#L14
    /// </remarks>
    public static bool EnableSensitiveDataDefault { get; } =
        Environment.GetEnvironmentVariable(KnownOtelConfigNames.InstrumentationGenAiCaptureMessageContent) is string envVar &&
        string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase);
}

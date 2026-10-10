// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Forwards unbuffered guardian diagnostics to the owner's logger without losing severity.
/// </summary>
internal sealed class ProcessSupervisorLogger(TextWriter writer) : ILogger
{
    private const string Prefix = "[aspire-process-supervisor] ";
    private readonly Lock _writeLock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Debug and < LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var diagnostic = new Diagnostic
        {
            Level = logLevel,
            SupervisorProcessId = Environment.ProcessId,
            Message = formatter(state, exception),
            Exception = exception?.ToString()
        };
        var line = Prefix + JsonSerializer.Serialize(diagnostic, ProcessSupervisorLoggerJsonContext.Default.Diagnostic);
        lock (_writeLock)
        {
            writer.WriteLine(line);
            // Unix cleanup kills the guardian itself. Do not depend on an asynchronous
            // logging queue surviving process-group termination.
            writer.Flush();
        }
    }

    internal static bool TryForward(string line, ILogger logger)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // The inherited stderr pipe mixes runtime output with guardian records:
        // [aspire-process-supervisor] {"Level":1,"SupervisorProcessId":123,"Message":"...","Exception":null}
        // JSON escaping keeps multiline exceptions and quoted paths on one physical line.
        Diagnostic diagnostic;
        try
        {
            diagnostic = JsonSerializer.Deserialize(line.AsSpan(Prefix.Length), ProcessSupervisorLoggerJsonContext.Default.Diagnostic)
                ?? throw new JsonException("The supervisor diagnostic must be an object.");
            if (!Enum.IsDefined(diagnostic.Level) || diagnostic.Level == LogLevel.None || diagnostic.SupervisorProcessId <= 0)
            {
                throw new JsonException("The supervisor diagnostic has an invalid level or process ID.");
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed process supervisor diagnostic; preserving the original stderr line.");
            return false;
        }

        logger.Log(diagnostic.Level,
            "Process supervisor {SupervisorPid}: {Message}{DiagnosticException}",
            diagnostic.SupervisorProcessId, diagnostic.Message,
            diagnostic.Exception is null ? "" : Environment.NewLine + diagnostic.Exception);

        return true;
    }

    internal sealed class Diagnostic
    {
        public required LogLevel Level { get; init; }
        public required int SupervisorProcessId { get; init; }
        public required string Message { get; init; }
        public string? Exception { get; init; }
    }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ProcessSupervisorLogger.Diagnostic))]
internal sealed partial class ProcessSupervisorLoggerJsonContext : JsonSerializerContext;

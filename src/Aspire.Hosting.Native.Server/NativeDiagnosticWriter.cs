// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Server;

/// <summary>Writes classified host failures and opt-in correlated operation traces without caller data.</summary>
internal sealed class NativeDiagnosticWriter(bool verbose) : IObserver<OperationEvent>
{
    public void OnNext(OperationEvent operation)
    {
        if (!verbose && operation.Outcome != "error")
        {
            return;
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", operation.Timestamp);
            writer.WriteString("operationId", operation.OperationId);
            writer.WriteString("operation", operation.Operation);
            writer.WriteString("outcome", operation.Outcome);
            writer.WriteString("sessionId", operation.SessionId?.ToString());
            writer.WriteString("generationId", operation.GenerationId?.ToString());
            writer.WriteString("resourceId", operation.ResourceId?.ToString());
            writer.WriteString("traceId", operation.TraceId);
            writer.WriteString("errorType", operation.ErrorType);
            writer.WriteNumber("durationSeconds", operation.DurationSeconds);
            writer.WriteEndObject();
        }
        Console.Error.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public void OnError(Exception error) => Console.Error.WriteLine($"Native diagnostic stream failed: {error.GetType().Name}.");
    public void OnCompleted()
    {
    }
}

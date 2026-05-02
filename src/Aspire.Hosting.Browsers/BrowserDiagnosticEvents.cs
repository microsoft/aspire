// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal abstract record BrowserDiagnosticEvent;

internal sealed record BrowserConsoleDiagnosticEvent(string Level, string Message) : BrowserDiagnosticEvent;

internal sealed record BrowserExceptionDiagnosticEvent(string Message, BrowserDiagnosticSourceLocation? Location) : BrowserDiagnosticEvent;

internal sealed record BrowserLogEntryDiagnosticEvent(string Level, string Text, BrowserDiagnosticSourceLocation? Location) : BrowserDiagnosticEvent;

internal sealed record BrowserNetworkRequestStartedDiagnosticEvent(
    string RequestId,
    string Method,
    string Url,
    string? ResourceType,
    double? Timestamp,
    BrowserNetworkResponseDetails? RedirectResponse) : BrowserDiagnosticEvent;

internal sealed record BrowserNetworkResponseReceivedDiagnosticEvent(
    string RequestId,
    string? ResourceType,
    BrowserNetworkResponseDetails? Response) : BrowserDiagnosticEvent;

internal sealed record BrowserNetworkRequestCompletedDiagnosticEvent(
    string RequestId,
    double? Timestamp,
    double? EncodedDataLength) : BrowserDiagnosticEvent;

internal sealed record BrowserNetworkRequestFailedDiagnosticEvent(
    string RequestId,
    double? Timestamp,
    string? ErrorText,
    string? BlockedReason,
    bool? Canceled) : BrowserDiagnosticEvent;

internal sealed record BrowserDiagnosticSourceLocation(string? Url, int? LineNumber, int? ColumnNumber);

internal sealed record BrowserNetworkResponseDetails(
    string? Url,
    int? Status,
    string? StatusText,
    bool? FromDiskCache,
    bool? FromServiceWorker);

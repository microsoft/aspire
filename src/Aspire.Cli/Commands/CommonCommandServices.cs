// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class CommonCommandServices(
    IFeatures features,
    ICliUpdateNotifier updateNotifier,
    CliExecutionContext executionContext,
    IInteractionService interactionService,
    AspireCliTelemetry telemetry,
    ConsoleCancellationManager cancellationManager,
    ILoggerFactory loggerFactory,
    ICliHostEnvironment hostEnvironment)
{
    public IFeatures Features { get; } = features;
    public ICliUpdateNotifier UpdateNotifier { get; } = updateNotifier;
    public CliExecutionContext ExecutionContext { get; } = executionContext;
    public IInteractionService InteractionService { get; } = interactionService;
    public AspireCliTelemetry Telemetry { get; } = telemetry;
    public ConsoleCancellationManager CancellationManager { get; } = cancellationManager;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ICliHostEnvironment HostEnvironment { get; } = hostEnvironment;
}

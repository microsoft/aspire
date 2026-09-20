// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;

internal sealed class TestEfTool
{
    public DotnetToolResource Resource { get; } = new("test-ef-tool", "dotnet-ef");

    public ConcurrentQueue<string[]> Invocations { get; } = new();

    public ExecuteCommandResult Result { get; set; } = CommandResults.Success();

    public TestEfTool()
    {
        Resource.Annotations.Add(new ResourceCommandAnnotation(
            KnownResourceCommands.StartCommand,
            "Start",
            _ => ResourceCommandState.Enabled,
            async context =>
            {
                var arguments = await EFResourceBuilderExtensions.GatherToolArgumentsAsync(
                    Resource,
                    context.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                    context.Logger,
                    context.CancellationToken);
                Invocations.Enqueue(arguments.Select(argument => argument.ToString()!).ToArray());
                await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                    Resource,
                    snapshot => snapshot with
                    {
                        State = Result.Success ? KnownResourceStates.Finished : KnownResourceStates.FailedToStart,
                        ExitCode = Result.Success ? 0 : 1
                    });
                return Result;
            },
            displayDescription: null,
            arguments: null,
            confirmationMessage: null,
            iconName: null,
            iconVariant: null,
            isHighlighted: false));
    }
}

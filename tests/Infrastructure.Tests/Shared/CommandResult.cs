// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Infrastructure.Tests;

/// <summary>Result of a command execution.</summary>
public readonly struct CommandResult
{
    public ProcessStartInfo StartInfo { get; }
    public int ExitCode { get; }
    public string Output { get; }

    public CommandResult(ProcessStartInfo startInfo, int exitCode, string output)
    {
        StartInfo = startInfo;
        ExitCode = exitCode;
        Output = output;
    }

    public CommandResult EnsureSuccessful(string messagePrefix = "")
        => EnsureExitCode(expectedExitCode: 0, messagePrefix);

    public CommandResult EnsureExitCode(int expectedExitCode, string messagePrefix = "")
    {
        if (ExitCode != expectedExitCode)
        {
            var message = $"{messagePrefix} Expected {expectedExitCode} exit code but got {ExitCode}: {StartInfo.FileName} {StartInfo.Arguments}";
            if (!string.IsNullOrEmpty(Output))
            {
                message += $"{System.Environment.NewLine}Output:{System.Environment.NewLine}{Output}";
            }
            throw new CommandException(message, this);
        }
        return this;
    }
}

/// <summary>Exception thrown when a command fails.</summary>
public class CommandException : Exception
{
    public CommandResult Result { get; }

    public CommandException(string message, CommandResult result) : base(message)
    {
        Result = result;
    }
}

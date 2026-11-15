// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests;

internal static class TestHelpers
{
    public static ConsoleEnvironment CreateConsoleEnvironment(TextWriter output, TextWriter error) =>
        new(
            CliServiceCollectionTestOptions.CreateAnsiConsole(output, ansi: false),
            CliServiceCollectionTestOptions.CreateAnsiConsole(error, ansi: false),
            TextReader.Null);

    public static ICliHostEnvironment CreateInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: true, supportsInteractiveOutput: true, supportsAnsi: true);
    }

    public static ICliHostEnvironment CreateNonInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: false, supportsInteractiveOutput: false, supportsAnsi: false);
    }
}

internal sealed class TestCliHostEnvironment(bool supportsInteractiveInput = false, bool supportsInteractiveOutput = false, bool supportsAnsi = true) : ICliHostEnvironment
{
    public bool SupportsInteractiveInput => supportsInteractiveInput;

    public bool SupportsInteractiveOutput => supportsInteractiveOutput;

    public bool SupportsAnsi => supportsAnsi;
}

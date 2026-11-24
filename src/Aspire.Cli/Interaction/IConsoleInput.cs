// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

internal interface IConsoleInput
{
    bool IsInputRedirected { get; }
    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey(bool intercept);
}

internal sealed class ConsoleInput : IConsoleInput
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool KeyAvailable => Console.KeyAvailable;
    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.SelectTests;

// Thin entry point. The CLI wiring (argument parsing, invoking the graph tool for Layer 1,
// reading the matrix JSON, emitting the filtered matrix + per-job booleans) is added together
// with the TestSelector implementation.
internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("SelectTests is not implemented yet.");
        return 2;
    }
}

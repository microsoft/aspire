// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests;

internal static class TestHelpers
{
    public static ICliHostEnvironment CreateInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: true, supportsInteractiveOutput: true, supportsAnsi: true);
    }

    public static ICliHostEnvironment CreateNonInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: false, supportsInteractiveOutput: false, supportsAnsi: false);
    }

    private sealed class TestCliHostEnvironment(bool supportsInteractiveInput, bool supportsInteractiveOutput, bool supportsAnsi) : ICliHostEnvironment
    {
        public bool SupportsInteractiveInput => supportsInteractiveInput;
        public bool SupportsInteractiveOutput => supportsInteractiveOutput;
        public bool SupportsAnsi => supportsAnsi;
    }
}

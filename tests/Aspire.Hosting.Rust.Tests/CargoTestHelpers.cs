// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Rust.Tests;

internal static class CargoTestHelpers
{
    public static void SkipIfUnavailable()
    {
        if (PathLookupHelper.FindFullPathFromPath("cargo") is null)
        {
            Assert.Skip("'cargo' is not available on PATH.");
        }
    }
}

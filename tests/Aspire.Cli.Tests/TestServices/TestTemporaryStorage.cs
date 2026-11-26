// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.Tests;

/// <summary>
/// Creates the temporary storage used by shared process tests.
/// </summary>
internal static class TestTemporaryStorage
{
    public static ITemporaryStorage Create() => new TemporaryStorage();
}

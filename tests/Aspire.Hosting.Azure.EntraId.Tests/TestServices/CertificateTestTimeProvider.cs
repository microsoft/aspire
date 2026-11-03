// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.EntraId.Tests.TestServices;

internal sealed class CertificateTestTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

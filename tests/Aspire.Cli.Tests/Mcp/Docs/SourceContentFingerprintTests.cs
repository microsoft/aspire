// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation;

namespace Aspire.Cli.Tests.Documentation;

public class SourceContentFingerprintTests
{
    [Fact]
    public void Compute_SameContentAndVersion_ReturnsSameFingerprint()
    {
        var a = SourceContentFingerprint.Compute("# Doc\nbody", schemaVersion: 1);
        var b = SourceContentFingerprint.Compute("# Doc\nbody", schemaVersion: 1);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DifferentContent_ReturnsDifferentFingerprint()
    {
        var a = SourceContentFingerprint.Compute("# Doc A", schemaVersion: 1);
        var b = SourceContentFingerprint.Compute("# Doc B", schemaVersion: 1);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentSchemaVersion_ReturnsDifferentFingerprint()
    {
        // The whole point of the schema version: bumping the constant must invalidate
        // previously-cached fingerprints even when the input content is byte-identical.
        var v1 = SourceContentFingerprint.Compute("# Doc\nbody", schemaVersion: 1);
        var v2 = SourceContentFingerprint.Compute("# Doc\nbody", schemaVersion: 2);

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void Compute_ProducesLowercaseHex()
    {
        var fingerprint = SourceContentFingerprint.Compute("hello", schemaVersion: 1);

        Assert.NotEmpty(fingerprint);
        Assert.All(fingerprint, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f'), $"Unexpected char: '{c}'"));
    }

    [Fact]
    public void Compute_NullContent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SourceContentFingerprint.Compute(null!, schemaVersion: 1));
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class IdentityChannelReaderTests
{
    private const string ChannelMetadataKey = "AspireCliChannel";

    [Fact]
    public void Ctor_NullAssembly_ThrowsArgumentNullException()
    {
        // Explicit null produces an immediate, descriptive ArgumentNullException so misuse
        // is caught at construction time rather than surfacing later as the cryptic
        // "metadata missing on '?'" exception.
        Assert.Throws<ArgumentNullException>(() => new IdentityChannelReader(null!));
    }

    [Fact]
    public void ReadChannel_AssemblyHasValidChannelMetadata_ReturnsValue()
    {
        // Integration smoke for the reader → IsValidChannel wiring. The full shape truth
        // table is asserted by IsValidChannel_MatchesExpectedTruthTable below; this test
        // proves a valid value round-trips through a fake assembly's
        // [AssemblyMetadata("AspireCliChannel", ...)].
        var assembly = BuildFakeAssemblyWithChannelMetadata("FakeCli_Valid", "pr-12345");

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("pr-12345", reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_AssemblyMissingChannelMetadata_ThrowsWithAssemblyName()
    {
        const string assemblyName = "FakeCli_NoChannel";
        var assembly = BuildFakeAssemblyWithChannelMetadata(assemblyName, channelValue: null);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(assemblyName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadChannel_AssemblyHasInvalidChannelMetadata_Throws()
    {
        // Integration smoke that an invalid baked value surfaces as InvalidOperationException
        // through the reader. The exhaustive invalid-value matrix is in
        // IsValidChannel_MatchesExpectedTruthTable below.
        var assembly = BuildFakeAssemblyWithChannelMetadata("FakeCli_Invalid", "pr");

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadChannel_AssemblyHasMultipleChannelMetadataAttributes_ReturnsFirstNonEmpty()
    {
        // MSBuild misconfiguration could conceivably emit two AspireCliChannel entries.
        // The reader uses FirstOrDefault, so the first attribute encountered wins. Document
        // this so future changes don't silently flip ordering.
        var assembly = BuildFakeAssemblyWithChannelMetadata(
            "FakeCli_DualChannel",
            channelValues: ["staging", "pr-12345"]);

        var reader = new IdentityChannelReader(assembly);

        Assert.Equal("staging", reader.ReadChannel());
    }

    [Fact]
    public void ReadChannel_KeyLookupIsCaseSensitive_DifferentCaseTreatedAsMissing()
    {
        var assembly = BuildFakeAssemblyWithMetadata(
            "FakeCli_CaseMismatch",
            metadata: [(Key: "aspirecliChannel", Value: "stable")]);

        var reader = new IdentityChannelReader(assembly);

        var ex = Assert.Throws<InvalidOperationException>(reader.ReadChannel);
        Assert.Contains(ChannelMetadataKey, ex.Message, StringComparison.Ordinal);
    }

    // IsValidChannel is the gate for the baked value. We assert its exact
    // truth table here so the reader's contract is testable without round-
    // tripping through a fake assembly. The smoke tests above cover the
    // valid-roundtrip and invalid-throws integration through ReadChannel.
    [Theory]
    [InlineData("stable", true)]
    [InlineData("staging", true)]
    [InlineData("daily", true)]
    [InlineData("local", true)]
    [InlineData("local-super-parakeet", true)]
    [InlineData("local-worktree-123", true)]
    [InlineData("pr-1", true)]
    [InlineData("pr-12345", true)]
    [InlineData("pr-0", true)]                                  // zero is permitted; build pipeline never emits it but the reader does not gate on positivity
    [InlineData("pr-99999999999999999999", true)]               // arbitrarily long digit run is accepted; range check is not the reader's job
    [InlineData("pr-0123", true)]                               // leading zeros are permitted; locks in the "any ASCII-digit run" contract
    [InlineData("pr", false)]                                   // legacy literal — the change this test guards against regressing
    [InlineData("pr-", false)]
    [InlineData("pr-abc", false)]
    [InlineData("pr-12a", false)]
    [InlineData("pr-12.34", false)]
    [InlineData("pr-+1", false)]                                // sign chars are not ASCII digits — guards against a "support signed numerics" regression
    [InlineData("pr--1", false)]
    [InlineData("pr-1 2", false)]                               // embedded space inside the digit portion
    [InlineData("pr-1\n", false)]                               // trailing control char inside the digit portion
    [InlineData("pr-١", false)]                                 // Arabic-Indic digit U+0661 — ASCII-only contract, not Unicode-digit
    [InlineData("Pr-12345", false)]                             // case-sensitive prefix
    [InlineData("PR-12345", false)]
    [InlineData("Stable", false)]                               // literals are case-sensitive (ordinal)
    [InlineData("STABLE", false)]
    [InlineData("Local", false)]
    [InlineData(" stable", false)]                              // leading whitespace on a literal — symmetric with the existing pr-12345 whitespace cases
    [InlineData("stable ", false)]
    [InlineData("local-", false)]
    [InlineData("local-Foo", false)]
    [InlineData("local-worktree_123", false)]
    [InlineData("local-worktree.123", false)]
    [InlineData("foobar", false)]
    [InlineData("default", false)]                              // PackageChannelNames.Default is for runtime PSM, never baked
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(" pr-12345", false)]
    [InlineData("pr-12345 ", false)]
    public void IsValidChannel_MatchesExpectedTruthTable(string value, bool expected)
    {
        Assert.Equal(expected, IdentityChannelReader.IsValidChannel(value));
    }

    private static Assembly BuildFakeAssemblyWithChannelMetadata(string assemblyName, string? channelValue)
    {
        if (channelValue is null)
        {
            return BuildFakeAssemblyWithMetadata(assemblyName, metadata: []);
        }

        return BuildFakeAssemblyWithMetadata(
            assemblyName,
            metadata: [(Key: ChannelMetadataKey, Value: channelValue)]);
    }

    private static Assembly BuildFakeAssemblyWithChannelMetadata(string assemblyName, string[] channelValues)
    {
        var metadata = channelValues
            .Select(v => (Key: ChannelMetadataKey, Value: v))
            .ToArray();
        return BuildFakeAssemblyWithMetadata(assemblyName, metadata);
    }

    private static Assembly BuildFakeAssemblyWithMetadata(string assemblyName, (string Key, string Value)[] metadata)
    {
        var name = new AssemblyName(assemblyName);
        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        var attributeCtor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in metadata)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, [key, value]));
        }

        return builder;
    }
}

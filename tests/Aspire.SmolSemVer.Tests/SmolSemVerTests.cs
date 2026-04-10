// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.SmolSemVer.Tests;

/// <summary>
/// SemVer 2.0.0 Spec Rule 2: A normal version number MUST take the form X.Y.Z where X, Y, and Z are
/// non-negative integers, and MUST NOT contain leading zeroes.
/// </summary>
public class SpecRule2_NormalVersionFormatTests
{
    [Theory]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("999.999.999", 999, 999, 999)]
    [InlineData("0.0.4", 0, 0, 4)]
    [InlineData("1.0.0", 1, 0, 0)]
    public void Parse_ValidVersions(string input, int major, int minor, int patch)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);

        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.False(v.IsPrerelease);
        Assert.Equal(string.Empty, v.Prerelease);
        Assert.Equal(string.Empty, v.Metadata);
    }

    [Theory]
    [InlineData("01.0.0")]   // Leading zero in major
    [InlineData("0.01.0")]   // Leading zero in minor
    [InlineData("0.0.01")]   // Leading zero in patch
    [InlineData("001.002.003")]
    public void Parse_Strict_RejectsLeadingZeros(string input)
    {
        Assert.False(SemVersion.TryParse(input, SemVersionStyles.Strict, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]           // Missing minor and patch
    [InlineData("1.2")]         // Missing patch
    [InlineData("a.b.c")]       // Non-numeric
    [InlineData("-1.0.0")]      // Negative (leading dash parsed as prerelease fail)
    [InlineData("1.0.0.0")]     // Four parts
    [InlineData(" 1.0.0")]      // Leading space
    [InlineData("1.0.0 ")]      // Trailing space
    [InlineData("v1.0.0")]      // v-prefix in strict mode
    [InlineData("V1.0.0")]      // V-prefix in strict mode
    public void Parse_Strict_RejectsInvalidFormats(string input)
    {
        Assert.False(SemVersion.TryParse(input, SemVersionStyles.Strict, out _));
        Assert.Throws<FormatException>(() => SemVersion.Parse(input, SemVersionStyles.Strict));
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SemVersion.Parse(null!, SemVersionStyles.Strict));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(SemVersion.TryParse(null, SemVersionStyles.Strict, out var v));
        Assert.Null(v);
    }

    [Fact]
    public void Parse_LargeVersionNumbers()
    {
        var v = SemVersion.Parse("2147483647.2147483647.2147483647", SemVersionStyles.Strict);
        Assert.Equal(int.MaxValue, v.Major);
        Assert.Equal(int.MaxValue, v.Minor);
        Assert.Equal(int.MaxValue, v.Patch);
    }

    [Fact]
    public void Parse_OverflowVersionNumber_Fails()
    {
        Assert.False(SemVersion.TryParse("2147483648.0.0", SemVersionStyles.Strict, out _));
    }

    /// <summary>
    /// Rule 2: Each element MUST increase numerically. For instance: 1.9.0 -> 1.10.0 -> 1.11.0.
    /// (Tests that 1.10.0 > 1.9.0 — i.e. numeric not lexicographic.)
    /// </summary>
    [Fact]
    public void Precedence_NumericNotLexicographic()
    {
        var v190 = SemVersion.Parse("1.9.0", SemVersionStyles.Strict);
        var v1100 = SemVersion.Parse("1.10.0", SemVersionStyles.Strict);
        var v1110 = SemVersion.Parse("1.11.0", SemVersionStyles.Strict);

        Assert.True(v190.IsOlderThan(v1100));
        Assert.True(v1100.IsOlderThan(v1110));
    }
}

/// <summary>
/// SemVer 2.0.0 Spec Rule 9: Pre-release version — hyphen + dot-separated identifiers.
/// Identifiers MUST comprise only ASCII alphanumerics and hyphens [0-9A-Za-z-].
/// Identifiers MUST NOT be empty. Numeric identifiers MUST NOT include leading zeroes.
/// Pre-release versions have lower precedence than the associated normal version.
/// </summary>
public class SpecRule9_PrereleaseTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "alpha")]
    [InlineData("1.0.0-alpha.1", "alpha.1")]
    [InlineData("1.0.0-0.3.7", "0.3.7")]
    [InlineData("1.0.0-x.7.z.92", "x.7.z.92")]
    [InlineData("1.0.0-x-y-z.--", "x-y-z.--")]           // Spec example: hyphens-only identifier
    [InlineData("1.0.0-alpha-beta", "alpha-beta")]
    [InlineData("1.0.0-beta.11", "beta.11")]
    [InlineData("1.0.0-rc.1", "rc.1")]
    [InlineData("1.0.0-0", "0")]                           // Single zero is valid
    [InlineData("10.0.100-preview.1.25463.5", "preview.1.25463.5")]
    public void Parse_ValidPrerelease(string input, string expectedPrerelease)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);

        Assert.True(v.IsPrerelease);
        Assert.Equal(expectedPrerelease, v.Prerelease);
    }

    [Theory]
    [InlineData("1.0.0-01")]        // Leading zero in numeric prerelease identifier
    [InlineData("1.0.0-")]          // Empty prerelease (trailing hyphen)
    [InlineData("1.0.0-alpha..1")]  // Empty identifier between dots
    [InlineData("1.0.0-.alpha")]    // Empty identifier at start
    [InlineData("1.0.0-alpha.")]    // Empty identifier at end (trailing dot)
    [InlineData("1.0.0- ")]         // Space in prerelease
    [InlineData("1.0.0-al pha")]    // Space inside identifier
    [InlineData("1.0.0-alpha!")]    // Invalid character
    public void Parse_Strict_RejectsInvalidPrerelease(string input)
    {
        Assert.False(SemVersion.TryParse(input, SemVersionStyles.Strict, out _));
    }

    [Fact]
    public void Prerelease_HasLowerPrecedenceThanRelease()
    {
        var prerelease = SemVersion.Parse("1.0.0-alpha", SemVersionStyles.Strict);
        var release = SemVersion.Parse("1.0.0", SemVersionStyles.Strict);

        Assert.True(prerelease.IsOlderThan(release));
        Assert.True(release.IsNewerThan(prerelease));
    }

    [Theory]
    [InlineData("1.0.0-alpha.0beta")]  // Mixed alpha-numeric identifier
    [InlineData("1.0.0-alpha.0")]      // 0 in dotted prerelease is fine (single zero)
    public void Parse_Strict_AcceptsMixedPrereleaseIdentifiers(string input)
    {
        Assert.True(SemVersion.TryParse(input, SemVersionStyles.Strict, out var v));
        Assert.NotNull(v);
    }

    [Fact]
    public void Parse_LongPrerelease()
    {
        var prerelease = "alpha.1.2.3.4.5.6.7.8.9.10";
        var v = SemVersion.Parse($"1.0.0-{prerelease}", SemVersionStyles.Strict);
        Assert.Equal(prerelease, v.Prerelease);
    }
}

/// <summary>
/// SemVer 2.0.0 Spec Rule 10: Build metadata — plus sign + dot-separated identifiers.
/// Identifiers MUST comprise only ASCII alphanumerics and hyphens [0-9A-Za-z-].
/// Identifiers MUST NOT be empty. Build metadata MUST be ignored when determining version precedence.
/// </summary>
public class SpecRule10_BuildMetadataTests
{
    [Theory]
    [InlineData("1.0.0+build", "", "build")]
    [InlineData("1.0.0+20130313144700", "", "20130313144700")]
    [InlineData("1.0.0+exp.sha.5114f85", "", "exp.sha.5114f85")]
    [InlineData("1.0.0-beta+exp.sha.5114f85", "beta", "exp.sha.5114f85")]
    [InlineData("1.0.0-alpha.1+001", "alpha.1", "001")]     // Leading zeros OK in metadata
    [InlineData("1.0.0+21AF26D3----117B344092BD", "", "21AF26D3----117B344092BD")] // Spec example: consecutive hyphens
    public void Parse_WithMetadata(string input, string expectedPrerelease, string expectedMetadata)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);

        Assert.Equal(expectedPrerelease, v.Prerelease);
        Assert.Equal(expectedMetadata, v.Metadata);
    }

    [Theory]
    [InlineData("1.0.0+")]          // Empty metadata
    [InlineData("1.0.0+build..1")]  // Empty identifier between dots
    [InlineData("1.0.0+.build")]    // Empty identifier at start
    [InlineData("1.0.0+build.")]    // Empty identifier at end
    [InlineData("1.0.0+build!")]    // Invalid character
    public void Parse_Strict_RejectsInvalidMetadata(string input)
    {
        Assert.False(SemVersion.TryParse(input, SemVersionStyles.Strict, out _));
    }

    [Fact]
    public void Precedence_BuildMetadata_IsIgnored()
    {
        var v1 = SemVersion.Parse("1.0.0+build.1", SemVersionStyles.Strict);
        var v2 = SemVersion.Parse("1.0.0+build.2", SemVersionStyles.Strict);

        Assert.True(v1.HasSamePrecedenceAs(v2));
    }

    [Fact]
    public void Precedence_BuildMetadata_StableOnly()
    {
        var v = SemVersion.Parse("1.0.0+build", SemVersionStyles.Strict);
        Assert.False(v.IsPrerelease);
    }

    [Fact]
    public void Precedence_BuildMetadata_WithPrerelease()
    {
        var v = SemVersion.Parse("1.0.0-rc.1+build", SemVersionStyles.Strict);
        Assert.True(v.IsPrerelease);
    }
}

/// <summary>
/// SemVer 2.0.0 Spec Rule 11: Precedence — how versions are compared when ordered.
/// Covers 11.1 through 11.4 with the spec's own example ordering.
/// </summary>
public class SpecRule11_PrecedenceTests
{
    /// <summary>
    /// SemVer 2.0.0 §11 complete example:
    /// 1.0.0-alpha &lt; 1.0.0-alpha.1 &lt; 1.0.0-alpha.beta &lt; 1.0.0-beta
    /// &lt; 1.0.0-beta.2 &lt; 1.0.0-beta.11 &lt; 1.0.0-rc.1 &lt; 1.0.0
    /// </summary>
    [Fact]
    public void Precedence_MatchesSemVerSpec_Section11_FullExample()
    {
        var versions = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0"
        };

        for (var i = 0; i < versions.Length - 1; i++)
        {
            var lower = SemVersion.Parse(versions[i], SemVersionStyles.Strict);
            var higher = SemVersion.Parse(versions[i + 1], SemVersionStyles.Strict);

            Assert.True(lower.IsOlderThan(higher), $"{versions[i]} should be older than {versions[i + 1]}");
            Assert.True(higher.IsNewerThan(lower), $"{versions[i + 1]} should be newer than {versions[i]}");
            Assert.False(lower.HasSamePrecedenceAs(higher));
        }
    }

    /// <summary>
    /// Rule 11.2: Major, minor, and patch versions are always compared numerically.
    /// Example: 1.0.0 &lt; 2.0.0 &lt; 2.1.0 &lt; 2.1.1.
    /// </summary>
    [Fact]
    public void Precedence_SpecRule11_2_NumericComparison()
    {
        var versions = new[] { "1.0.0", "2.0.0", "2.1.0", "2.1.1" };

        for (var i = 0; i < versions.Length - 1; i++)
        {
            var lower = SemVersion.Parse(versions[i], SemVersionStyles.Strict);
            var higher = SemVersion.Parse(versions[i + 1], SemVersionStyles.Strict);
            Assert.True(lower.IsOlderThan(higher), $"{versions[i]} should be older than {versions[i + 1]}");
        }
    }

    /// <summary>
    /// Rule 11.3: When major, minor, and patch are equal, a pre-release version has
    /// lower precedence than a normal version. Example: 1.0.0-alpha &lt; 1.0.0.
    /// </summary>
    [Fact]
    public void Precedence_SpecRule11_3_PrereleaseVsNormal()
    {
        var prerelease = SemVersion.Parse("1.0.0-alpha", SemVersionStyles.Strict);
        var release = SemVersion.Parse("1.0.0", SemVersionStyles.Strict);

        Assert.True(prerelease.IsOlderThan(release));
    }

    /// <summary>
    /// Rule 11.4.1: Identifiers consisting of only digits are compared numerically.
    /// </summary>
    [Theory]
    [InlineData("1.0.0-1", "1.0.0-2")]
    [InlineData("1.0.0-2", "1.0.0-10")]    // Numeric, not lexicographic (2 < 10)
    [InlineData("1.0.0-0", "1.0.0-1")]
    public void Precedence_SpecRule11_4_1_NumericIdentifiers(string older, string newer)
    {
        var v1 = SemVersion.Parse(older, SemVersionStyles.Strict);
        var v2 = SemVersion.Parse(newer, SemVersionStyles.Strict);
        Assert.True(v1.IsOlderThan(v2));
    }

    /// <summary>
    /// Rule 11.4.2: Identifiers with letters or hyphens are compared lexically in ASCII sort order.
    /// </summary>
    [Theory]
    [InlineData("1.0.0-a", "1.0.0-b")]
    [InlineData("1.0.0-A", "1.0.0-a")]       // Uppercase < lowercase in ASCII
    [InlineData("1.0.0-aaa", "1.0.0-b")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    public void Precedence_SpecRule11_4_2_AlphanumericIdentifiers(string older, string newer)
    {
        var v1 = SemVersion.Parse(older, SemVersionStyles.Strict);
        var v2 = SemVersion.Parse(newer, SemVersionStyles.Strict);
        Assert.True(v1.IsOlderThan(v2));
    }

    /// <summary>
    /// Rule 11.4.3: Numeric identifiers always have lower precedence than non-numeric identifiers.
    /// </summary>
    [Theory]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-999", "1.0.0-a")]
    public void Precedence_SpecRule11_4_3_NumericLowerThanAlphanumeric(string older, string newer)
    {
        var v1 = SemVersion.Parse(older, SemVersionStyles.Strict);
        var v2 = SemVersion.Parse(newer, SemVersionStyles.Strict);
        Assert.True(v1.IsOlderThan(v2));
    }

    /// <summary>
    /// Rule 11.4.4: A larger set of pre-release fields has a higher precedence than a smaller set,
    /// if all of the preceding identifiers are equal.
    /// </summary>
    [Fact]
    public void Precedence_SpecRule11_4_4_MoreFieldsHigherPrecedence()
    {
        var fewer = SemVersion.Parse("1.0.0-alpha", SemVersionStyles.Strict);
        var more = SemVersion.Parse("1.0.0-alpha.1", SemVersionStyles.Strict);

        Assert.True(fewer.IsOlderThan(more));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]          // Prerelease < release
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]  // Fewer fields < more fields
    public void Precedence_BasicOrdering(string older, string newer)
    {
        var v1 = SemVersion.Parse(older, SemVersionStyles.Strict);
        var v2 = SemVersion.Parse(newer, SemVersionStyles.Strict);

        Assert.True(v1.IsOlderThan(v2));
        Assert.True(v2.IsNewerThan(v1));
        Assert.False(v1.IsNewerThan(v2));
        Assert.False(v2.IsOlderThan(v1));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.1")]
    public void Precedence_EqualVersionsHaveSamePrecedence(string a, string b)
    {
        var v1 = SemVersion.Parse(a, SemVersionStyles.Strict);
        var v2 = SemVersion.Parse(b, SemVersionStyles.Strict);

        Assert.True(v1.HasSamePrecedenceAs(v2));
        Assert.True(v1.IsAtLeast(v2));
        Assert.False(v1.IsNewerThan(v2));
        Assert.False(v1.IsOlderThan(v2));
    }

    [Fact]
    public void PrecedenceComparer_SortsCorrectly()
    {
        var input = new[] { "3.0.0", "1.0.0", "2.0.0-beta", "2.0.0", "1.0.0-alpha" };
        var parsed = input.Select(s => SemVersion.Parse(s, SemVersionStyles.Strict)).ToList();

        var sorted = parsed.OrderBy(v => v, SemVersion.PrecedenceComparer).Select(v => v.ToString()).ToArray();

        Assert.Equal(["1.0.0-alpha", "1.0.0", "2.0.0-beta", "2.0.0", "3.0.0"], sorted);
    }

    [Fact]
    public void PrecedenceComparer_OrderByDescending_GivesNewestFirst()
    {
        var input = new[] { "1.0.0", "3.0.0", "2.0.0" };
        var parsed = input.Select(s => SemVersion.Parse(s, SemVersionStyles.Strict)).ToList();

        var sorted = parsed.OrderByDescending(v => v, SemVersion.PrecedenceComparer).Select(v => v.ToString()).ToArray();

        Assert.Equal(["3.0.0", "2.0.0", "1.0.0"], sorted);
    }

    [Fact]
    public void PrecedenceComparer_HandlesNulls()
    {
        Assert.True(SemVersion.PrecedenceComparer.Compare(null, SemVersion.Parse("1.0.0")) < 0);
        Assert.True(SemVersion.PrecedenceComparer.Compare(SemVersion.Parse("1.0.0"), null) > 0);
        Assert.Equal(0, SemVersion.PrecedenceComparer.Compare(null, null));
    }
}

/// <summary>
/// Tests for the expressive comparison methods: IsNewerThan, IsOlderThan, IsAtLeast, HasSamePrecedenceAs.
/// </summary>
public class ExplicitComparisonMethodTests
{
    [Fact]
    public void IsAtLeast_WhenEqual_ReturnsTrue()
    {
        var v = SemVersion.Parse("2.0.0", SemVersionStyles.Strict);
        var min = SemVersion.Parse("2.0.0", SemVersionStyles.Strict);
        Assert.True(v.IsAtLeast(min));
    }

    [Fact]
    public void IsAtLeast_WhenNewer_ReturnsTrue()
    {
        var v = SemVersion.Parse("3.0.0", SemVersionStyles.Strict);
        var min = SemVersion.Parse("2.0.0", SemVersionStyles.Strict);
        Assert.True(v.IsAtLeast(min));
    }

    [Fact]
    public void IsAtLeast_WhenOlder_ReturnsFalse()
    {
        var v = SemVersion.Parse("1.0.0", SemVersionStyles.Strict);
        var min = SemVersion.Parse("2.0.0", SemVersionStyles.Strict);
        Assert.False(v.IsAtLeast(min));
    }

    [Fact]
    public void IsOlderThan_AppHostBelowMinimum()
    {
        var aspireVersion = SemVersion.Parse("9.0.0", SemVersionStyles.Strict);
        var minimumVersion = SemVersion.Parse("9.2.0", SemVersionStyles.Strict);
        Assert.True(aspireVersion.IsOlderThan(minimumVersion));
    }

    [Fact]
    public void IsAtLeast_SdkMeetsMinimum()
    {
        var installed = SemVersion.Parse("10.0.200", SemVersionStyles.Strict);
        var required = SemVersion.Parse("10.0.100", SemVersionStyles.Strict);
        Assert.True(installed.IsAtLeast(required));
    }

    [Fact]
    public void IsNewerThan_FindsHighestVersion()
    {
        var versions = new[] { "1.0.0", "3.0.0", "2.0.0", "2.5.0" };

        SemVersion? highest = null;
        foreach (var vStr in versions)
        {
            var v = SemVersion.Parse(vStr, SemVersionStyles.Strict);
            if (highest is null || v.IsNewerThan(highest))
            {
                highest = v;
            }
        }

        Assert.Equal("3.0.0", highest!.ToString());
    }

    [Fact]
    public void PrecedenceComparer_RealWorldPackageSelection()
    {
        var packageVersions = new[] { "9.0.0", "9.1.0-preview.1", "9.1.0", "9.2.0-rc.1", "9.2.0" };

        var latest = packageVersions
            .Select(v => SemVersion.Parse(v, SemVersionStyles.Strict))
            .OrderByDescending(v => v, SemVersion.PrecedenceComparer)
            .First();

        Assert.Equal("9.2.0", latest.ToString());
    }

    [Fact]
    public void PrecedenceComparer_FilterAndSort_StableOnly()
    {
        var packageVersions = new[] { "9.0.0", "9.1.0-preview.1", "9.1.0", "9.2.0-rc.1", "9.2.0" };

        var latestStable = packageVersions
            .Select(v => SemVersion.Parse(v, SemVersionStyles.Strict))
            .Where(v => !v.IsPrerelease)
            .OrderByDescending(v => v, SemVersion.PrecedenceComparer)
            .First();

        Assert.Equal("9.2.0", latestStable.ToString());
    }
}

/// <summary>
/// Tests for <see cref="SemVersion.ToString()"/> round-trip formatting.
/// </summary>
public class ToStringTests
{
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.0.0+build")]
    [InlineData("1.0.0-beta+exp.sha.5114f85")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-0.3.7")]
    [InlineData("1.0.0-x.7.z.92")]
    [InlineData("1.0.0-x-y-z.--")]
    [InlineData("1.0.0+21AF26D3----117B344092BD")]
    public void ToString_RoundTrips(string input)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);
        Assert.Equal(input, v.ToString());
    }

    [Fact]
    public void ToString_FromConstructor()
    {
        var v = new SemVersion(1, 2, 3, "beta.1", "build.42");
        Assert.Equal("1.2.3-beta.1+build.42", v.ToString());
    }

    [Fact]
    public void ToString_StableFromConstructor()
    {
        var v = new SemVersion(1, 0, 0);
        Assert.Equal("1.0.0", v.ToString());
    }
}

/// <summary>
/// Tests for <see cref="SemVersion"/> equality and hashing.
/// </summary>
public class EqualityTests
{
    [Fact]
    public void Equals_SameVersion_ReturnsTrue()
    {
        var v1 = SemVersion.Parse("1.2.3-alpha+build", SemVersionStyles.Strict);
        var v2 = SemVersion.Parse("1.2.3-alpha+build", SemVersionStyles.Strict);

        Assert.Equal(v1, v2);
        Assert.True(v1 == v2);
        Assert.False(v1 != v2);
    }

    [Fact]
    public void Equals_DifferentMetadata_AreNotEqual()
    {
        var v1 = SemVersion.Parse("1.0.0+build.1", SemVersionStyles.Strict);
        var v2 = SemVersion.Parse("1.0.0+build.2", SemVersionStyles.Strict);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void Equals_DifferentVersions_AreNotEqual()
    {
        var v1 = SemVersion.Parse("1.0.0", SemVersionStyles.Strict);
        var v2 = SemVersion.Parse("1.0.1", SemVersionStyles.Strict);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void GetHashCode_SameVersion_SameHash()
    {
        var v1 = SemVersion.Parse("1.2.3-alpha+build", SemVersionStyles.Strict);
        var v2 = SemVersion.Parse("1.2.3-alpha+build", SemVersionStyles.Strict);
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var v = SemVersion.Parse("1.0.0", SemVersionStyles.Strict);
        Assert.False(v.Equals(null));
        Assert.False(v == null);
        Assert.True(v != null);
    }

    [Fact]
    public void Operators_NullOnBothSides()
    {
        SemVersion? a = null;
        SemVersion? b = null;
        Assert.True(a == b);
        Assert.False(a != b);
    }
}

/// <summary>
/// Tests for <see cref="SemVersion.IsPrerelease"/> property.
/// </summary>
public class IsPrereleaseTests
{
    [Theory]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0-alpha", true)]
    [InlineData("1.0.0-0", true)]
    [InlineData("1.0.0-alpha.1", true)]
    [InlineData("1.0.0+build", false)]          // Metadata only → stable
    [InlineData("1.0.0-rc.1+build", true)]      // Prerelease + metadata → prerelease
    [InlineData("10.0.100-preview.1.25463.5", true)]
    public void IsPrerelease_CorrectlyIdentifies(string input, bool expected)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);
        Assert.Equal(expected, v.IsPrerelease);
    }
}

/// <summary>
/// Tests for the <see cref="SemVersion(int, int, int, string, string)"/> constructor,
/// including input validation (negative numbers, invalid strings).
/// </summary>
public class ConstructorTests
{
    [Fact]
    public void Constructor_DefaultsToStable()
    {
        var v = new SemVersion(1, 2, 3);

        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.False(v.IsPrerelease);
        Assert.Equal(string.Empty, v.Prerelease);
        Assert.Equal(string.Empty, v.Metadata);
    }

    [Fact]
    public void Constructor_MinorAndPatchDefaultToZero()
    {
        var v = new SemVersion(5);
        Assert.Equal(5, v.Major);
        Assert.Equal(0, v.Minor);
        Assert.Equal(0, v.Patch);
    }

    [Fact]
    public void Constructor_WithAllParts()
    {
        var v = new SemVersion(1, 2, 3, "beta.1", "build.42");

        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.Equal("beta.1", v.Prerelease);
        Assert.Equal("build.42", v.Metadata);
        Assert.True(v.IsPrerelease);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void Constructor_NegativeVersionComponents_Throws(int major, int minor, int patch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemVersion(major, minor, patch));
    }

    [Theory]
    [InlineData("01")]         // Leading zero in numeric identifier
    [InlineData("!!!")]        // Invalid characters
    [InlineData("alpha..1")]   // Empty identifier
    [InlineData("")]           // Empty string is allowed (means stable)
    public void Constructor_InvalidPrerelease_Throws(string prerelease)
    {
        if (prerelease.Length == 0)
        {
            // Empty is valid — means stable
            var v = new SemVersion(1, 0, 0, prerelease: prerelease);
            Assert.False(v.IsPrerelease);
            return;
        }

        Assert.Throws<FormatException>(() => new SemVersion(1, 0, 0, prerelease: prerelease));
    }

    [Theory]
    [InlineData("!!!")]        // Invalid characters
    [InlineData("build..1")]   // Empty identifier
    public void Constructor_InvalidMetadata_Throws(string metadata)
    {
        Assert.Throws<FormatException>(() => new SemVersion(1, 0, 0, metadata: metadata));
    }
}

/// <summary>
/// Tests for lenient parsing modes (AllowLeadingV, AllowLeadingZeros, AllowMissingParts, Any).
/// </summary>
public class LenientParsingTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v0.0.0", 0, 0, 0)]
    public void Parse_Any_AllowsVPrefix(string input, int major, int minor, int patch)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Any);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory]
    [InlineData("01.2.3", 1, 2, 3)]
    [InlineData("1.02.3", 1, 2, 3)]
    [InlineData("1.2.03", 1, 2, 3)]
    [InlineData("001.002.003", 1, 2, 3)]
    public void Parse_Any_AllowsLeadingZeros(string input, int major, int minor, int patch)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Any);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory]
    [InlineData("1", 1, 0, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v1", 1, 0, 0)]
    [InlineData("v1.2", 1, 2, 0)]
    public void Parse_Any_AllowsMissingParts(string input, int major, int minor, int patch)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Any);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory]
    [InlineData("v1.2.3-alpha+build")]
    [InlineData("01.02.03-beta.1")]
    [InlineData("1-alpha")]
    [InlineData("1.2-rc.1")]
    public void Parse_Any_AllowsCombinedLenience(string input)
    {
        Assert.True(SemVersion.TryParse(input, SemVersionStyles.Any, out var v));
        Assert.NotNull(v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("v")]
    [InlineData("1.2.3.4")]
    [InlineData("..")]
    public void Parse_Any_StillRejectsGarbage(string input)
    {
        Assert.False(SemVersion.TryParse(input, SemVersionStyles.Any, out _));
    }

    [Fact]
    public void TryParse_DefaultStyle_UsesStrict()
    {
        Assert.True(SemVersion.TryParse("1.2.3", out var v));
        Assert.NotNull(v);
        Assert.False(SemVersion.TryParse("v1.2.3", out _));
    }
}

/// <summary>
/// Tests for individual <see cref="SemVersionStyles"/> flags.
/// </summary>
public class StylesFlagTests
{
    [Fact]
    public void AllowLeadingV_Only()
    {
        Assert.True(SemVersion.TryParse("v1.2.3", SemVersionStyles.AllowLeadingV, out _));
        Assert.False(SemVersion.TryParse("01.2.3", SemVersionStyles.AllowLeadingV, out _));
        Assert.False(SemVersion.TryParse("1.2", SemVersionStyles.AllowLeadingV, out _));
    }

    [Fact]
    public void AllowLeadingZeros_Only()
    {
        Assert.True(SemVersion.TryParse("01.02.03", SemVersionStyles.AllowLeadingZeros, out _));
        Assert.False(SemVersion.TryParse("v1.2.3", SemVersionStyles.AllowLeadingZeros, out _));
        Assert.False(SemVersion.TryParse("1.2", SemVersionStyles.AllowLeadingZeros, out _));
    }

    [Fact]
    public void AllowMissingParts_Only()
    {
        Assert.True(SemVersion.TryParse("1", SemVersionStyles.AllowMissingParts, out _));
        Assert.True(SemVersion.TryParse("1.2", SemVersionStyles.AllowMissingParts, out _));
        Assert.False(SemVersion.TryParse("v1.2.3", SemVersionStyles.AllowMissingParts, out _));
        Assert.False(SemVersion.TryParse("01.2.3", SemVersionStyles.AllowMissingParts, out _));
    }
}

/// <summary>
/// Tests verifying correctness against real-world Aspire version strings.
/// These replace the original Semver NuGet package compatibility tests with
/// hardcoded expected values (the SemVer 2.0.0 spec is stable).
/// </summary>
public class RealWorldVersionTests
{
    [Theory]
    [InlineData("9.0.0", 9, 0, 0, false, "")]
    [InlineData("9.2.0", 9, 2, 0, false, "")]
    [InlineData("10.0.0", 10, 0, 0, false, "")]
    [InlineData("13.0.0", 13, 0, 0, false, "")]
    [InlineData("9.2.0-preview.1", 9, 2, 0, true, "preview.1")]
    [InlineData("10.0.100-preview.1.25463.5", 10, 0, 100, true, "preview.1.25463.5")]
    [InlineData("13.1.0-preview.25206.3", 13, 1, 0, true, "preview.25206.3")]
    [InlineData("0.1.1", 0, 1, 1, false, "")]
    [InlineData("3.0.0", 3, 0, 0, false, "")]
    public void Parse_RealWorldVersions(string input, int major, int minor, int patch, bool isPrerelease, string prerelease)
    {
        var v = SemVersion.Parse(input, SemVersionStyles.Strict);

        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(isPrerelease, v.IsPrerelease);
        Assert.Equal(prerelease, v.Prerelease);
        Assert.Equal(input, v.ToString());
    }

    [Fact]
    public void Precedence_RealWorldOrdering()
    {
        // Verify the complete ordering of versions used in Aspire scenarios.
        var ordered = new[]
        {
            "0.0.1", "0.1.0", "0.1.1",
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta",
            "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0",
            "1.0.1", "1.1.0", "2.0.0",
            "9.0.0", "9.2.0-preview.1", "9.2.0",
            "10.0.100-preview.1.25463.5", "10.0.100",
            "13.0.0"
        };

        var parsed = ordered.Select(s => SemVersion.Parse(s, SemVersionStyles.Strict)).ToArray();

        for (var i = 0; i < parsed.Length - 1; i++)
        {
            Assert.True(parsed[i].IsOlderThan(parsed[i + 1]),
                $"{ordered[i]} should be older than {ordered[i + 1]}");
        }
    }

    [Fact]
    public void Sorting_RealWorldVersions()
    {
        var input = new[]
        {
            "3.0.0", "1.0.0-alpha", "2.0.0-beta", "1.0.0",
            "2.0.0", "1.0.0-alpha.1", "1.0.0-beta"
        };

        var sorted = input
            .Select(s => SemVersion.Parse(s, SemVersionStyles.Strict))
            .OrderBy(v => v, SemVersion.PrecedenceComparer)
            .Select(v => v.ToString())
            .ToArray();

        Assert.Equal(
            ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-beta", "1.0.0", "2.0.0-beta", "2.0.0", "3.0.0"],
            sorted);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("1.0.0-01", false)]    // Leading zero in numeric prerelease
    [InlineData("01.0.0", false)]       // Leading zero in strict
    [InlineData("1.0.0-", false)]       // Trailing hyphen
    [InlineData("1.0.0+", false)]       // Trailing plus
    [InlineData("1.0.0.0", false)]      // Four parts
    [InlineData("v1.0.0", false)]       // v-prefix in strict
    [InlineData("1.0.0", true)]         // Valid
    [InlineData("1.0.0-alpha", true)]   // Valid prerelease
    [InlineData("1.0.0+build", true)]   // Valid metadata
    public void TryParse_StrictValidation(string input, bool expectedResult)
    {
        Assert.Equal(expectedResult, SemVersion.TryParse(input, SemVersionStyles.Strict, out _));
    }
}

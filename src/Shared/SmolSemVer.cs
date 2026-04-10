// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Aspire;

/// <summary>
/// Controls how version strings are parsed.
/// </summary>
[Flags]
internal enum SemVersionStyles
{
    /// <summary>
    /// Strict SemVer 2.0.0 parsing. No leading zeros, no 'v' prefix.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Allow a leading 'v' or 'V' prefix (e.g. "v1.2.3").
    /// </summary>
    AllowLeadingV = 1,

    /// <summary>
    /// Allow leading zeros on numeric parts (e.g. "01.02.03").
    /// </summary>
    AllowLeadingZeros = 2,

    /// <summary>
    /// Allow missing minor and patch versions (e.g. "1" or "1.2").
    /// </summary>
    AllowMissingParts = 4,

    /// <summary>
    /// Accept any reasonable version string.
    /// </summary>
    Any = AllowLeadingV | AllowLeadingZeros | AllowMissingParts,
}

/// <summary>
/// A minimal, zero-dependency Semantic Versioning 2.0.0 implementation.
/// </summary>
/// <remarks>
/// Implements the SemVer 2.0.0 specification at https://semver.org/.
/// Uses <see cref="ReadOnlySpan{T}"/> to minimize allocations during parsing.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
internal sealed class SemVersion : IEquatable<SemVersion>, IComparable<SemVersion>
{
    /// <summary>
    /// A comparer that orders versions by SemVer 2.0.0 precedence rules.
    /// </summary>
    public static IComparer<SemVersion> PrecedenceComparer { get; } = new PrecedenceComparerImpl();

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>
    /// Gets the prerelease identifiers joined with '.', or an empty string if this is a stable release.
    /// </summary>
    public string Prerelease { get; }

    /// <summary>
    /// Gets the build metadata string, or an empty string if none.
    /// </summary>
    public string Metadata { get; }

    /// <summary>
    /// Gets whether this version has any prerelease identifiers.
    /// </summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    private readonly string[] _prereleaseIdentifiers;

    private SemVersion(int major, int minor, int patch, string prerelease, string[] prereleaseIdentifiers, string metadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Metadata = metadata;
        _prereleaseIdentifiers = prereleaseIdentifiers;
    }

    /// <summary>
    /// Creates a <see cref="SemVersion"/> from explicit components.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="major"/>, <paramref name="minor"/>, or <paramref name="patch"/> is negative.</exception>
    /// <exception cref="FormatException">Thrown when <paramref name="prerelease"/> or <paramref name="metadata"/> is not valid.</exception>
    public SemVersion(int major, int minor = 0, int patch = 0, string prerelease = "", string metadata = "")
        : this(
              major >= 0 ? major : throw new ArgumentOutOfRangeException(nameof(major), major, "Version component must not be negative."),
              minor >= 0 ? minor : throw new ArgumentOutOfRangeException(nameof(minor), minor, "Version component must not be negative."),
              patch >= 0 ? patch : throw new ArgumentOutOfRangeException(nameof(patch), patch, "Version component must not be negative."),
              ValidatePrerelease(prerelease),
              SplitIdentifiers(prerelease),
              ValidateMetadata(metadata))
    {
    }

    private static string ValidatePrerelease(string prerelease)
    {
        if (string.IsNullOrEmpty(prerelease))
        {
            return string.Empty;
        }

        var span = prerelease.AsSpan();
        var pos = 0;
        if (!TryConsumePrerelease(span, ref pos, SemVersionStyles.Strict) || pos != span.Length)
        {
            throw new FormatException($"The prerelease string '{prerelease}' is not valid.");
        }

        return prerelease;
    }

    private static string ValidateMetadata(string metadata)
    {
        if (string.IsNullOrEmpty(metadata))
        {
            return string.Empty;
        }

        var span = metadata.AsSpan();
        var pos = 0;
        if (!TryConsumeMetadata(span, ref pos) || pos != span.Length)
        {
            throw new FormatException($"The metadata string '{metadata}' is not valid.");
        }

        return metadata;
    }

    /// <summary>
    /// Parses a version string into a <see cref="SemVersion"/>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the string is not a valid semantic version.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="versionString"/> is null.</exception>
    public static SemVersion Parse(string versionString, SemVersionStyles styles = SemVersionStyles.Strict)
    {
        ArgumentNullException.ThrowIfNull(versionString);

        if (!TryParse(versionString, styles, out var version))
        {
            throw new FormatException($"The version string '{versionString}' is not a valid semantic version.");
        }

        return version;
    }

    /// <summary>
    /// Attempts to parse a version string into a <see cref="SemVersion"/>.
    /// </summary>
    public static bool TryParse(string? versionString, [NotNullWhen(true)] out SemVersion? version)
    {
        return TryParse(versionString, SemVersionStyles.Strict, out version);
    }

    /// <summary>
    /// Attempts to parse a version string into a <see cref="SemVersion"/>.
    /// </summary>
    public static bool TryParse(string? versionString, SemVersionStyles styles, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;

        if (string.IsNullOrEmpty(versionString))
        {
            return false;
        }

        return TryParseCore(versionString.AsSpan(), styles, out version);
    }

    private static bool TryParseCore(ReadOnlySpan<char> input, SemVersionStyles styles, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        var pos = 0;

        // Handle optional 'v'/'V' prefix.
        if (pos < input.Length && (input[pos] == 'v' || input[pos] == 'V'))
        {
            if ((styles & SemVersionStyles.AllowLeadingV) == 0)
            {
                return false;
            }
            pos++;
        }

        // Parse major version.
        if (!TryParseNumericPart(input, ref pos, styles, out var major))
        {
            return false;
        }

        int minor = 0, patch = 0;
        var allowMissing = (styles & SemVersionStyles.AllowMissingParts) != 0;

        if (pos < input.Length && input[pos] == '.')
        {
            pos++;
            if (!TryParseNumericPart(input, ref pos, styles, out minor))
            {
                return false;
            }

            if (pos < input.Length && input[pos] == '.')
            {
                pos++;
                if (!TryParseNumericPart(input, ref pos, styles, out patch))
                {
                    return false;
                }
            }
            else if (!allowMissing)
            {
                return false; // Strict requires all three parts.
            }
        }
        else if (!allowMissing)
        {
            return false; // Strict requires at least major.minor.
        }

        // Parse prerelease.
        var prerelease = ReadOnlySpan<char>.Empty;
        if (pos < input.Length && input[pos] == '-')
        {
            pos++;
            var start = pos;
            if (!TryConsumePrerelease(input, ref pos, styles))
            {
                return false;
            }
            prerelease = input[start..pos];
        }

        // Parse metadata.
        var metadata = ReadOnlySpan<char>.Empty;
        if (pos < input.Length && input[pos] == '+')
        {
            pos++;
            var start = pos;
            if (!TryConsumeMetadata(input, ref pos))
            {
                return false;
            }
            metadata = input[start..pos];
        }

        // Must have consumed the entire string.
        if (pos != input.Length)
        {
            return false;
        }

        var prereleaseStr = prerelease.Length > 0 ? prerelease.ToString() : string.Empty;
        var metadataStr = metadata.Length > 0 ? metadata.ToString() : string.Empty;

        version = new SemVersion(major, minor, patch, prereleaseStr, SplitIdentifiers(prereleaseStr), metadataStr);
        return true;
    }

    private static bool TryParseNumericPart(ReadOnlySpan<char> input, ref int pos, SemVersionStyles styles, out int value)
    {
        value = 0;
        var start = pos;

        while (pos < input.Length && input[pos] is >= '0' and <= '9')
        {
            pos++;
        }

        var length = pos - start;
        if (length == 0)
        {
            return false;
        }

        // Disallow leading zeros in strict mode.
        if (length > 1 && input[start] == '0' && (styles & SemVersionStyles.AllowLeadingZeros) == 0)
        {
            return false;
        }

        return int.TryParse(input[start..pos], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryConsumePrerelease(ReadOnlySpan<char> input, ref int pos, SemVersionStyles styles)
    {
        // Prerelease is dot-separated identifiers.
        // Each identifier is either numeric (no leading zeros in strict) or alphanumeric+hyphens.
        while (true)
        {
            var identStart = pos;

            while (pos < input.Length && IsIdentifierChar(input[pos]))
            {
                pos++;
            }

            if (pos == identStart)
            {
                return false; // Empty identifier.
            }

            var ident = input[identStart..pos];

            // If purely numeric, check for leading zeros in strict mode.
            if (IsAllDigits(ident) && ident.Length > 1 && ident[0] == '0' && (styles & SemVersionStyles.AllowLeadingZeros) == 0)
            {
                return false;
            }

            if (pos >= input.Length || input[pos] != '.')
            {
                break;
            }

            // Check that this dot is still in the prerelease section (not at end, and next char is not '+').
            if (pos + 1 >= input.Length || input[pos + 1] == '+')
            {
                return false; // Trailing dot in prerelease.
            }

            pos++; // Skip dot.
        }

        return true;
    }

    private static bool TryConsumeMetadata(ReadOnlySpan<char> input, ref int pos)
    {
        // Build metadata is dot-separated identifiers of alphanumeric characters and hyphens.
        // Empty identifiers are rejected.
        var start = pos;
        while (true)
        {
            var identStart = pos;

            while (pos < input.Length && IsIdentifierChar(input[pos]))
            {
                pos++;
            }

            if (pos == identStart)
            {
                return false; // Empty identifier.
            }

            if (pos >= input.Length || input[pos] != '.')
            {
                break;
            }

            pos++; // Skip dot.
        }

        return pos > start;
    }

    private static bool IsIdentifierChar(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-';

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static string[] SplitIdentifiers(string prerelease)
    {
        if (string.IsNullOrEmpty(prerelease))
        {
            return [];
        }

        return prerelease.Split('.');
    }

    /// <summary>
    /// Returns <see langword="true"/> when this version has strictly higher precedence than <paramref name="other"/>.
    /// </summary>
    public bool IsNewerThan(SemVersion other) => ComparePrecedenceTo(other) > 0;

    /// <summary>
    /// Returns <see langword="true"/> when this version has strictly lower precedence than <paramref name="other"/>.
    /// </summary>
    public bool IsOlderThan(SemVersion other) => ComparePrecedenceTo(other) < 0;

    /// <summary>
    /// Returns <see langword="true"/> when this version has equal or higher precedence than <paramref name="other"/>.
    /// </summary>
    public bool IsAtLeast(SemVersion other) => ComparePrecedenceTo(other) >= 0;

    /// <summary>
    /// Returns <see langword="true"/> when this version has the same precedence as <paramref name="other"/>
    /// (ignoring build metadata).
    /// </summary>
    public bool HasSamePrecedenceAs(SemVersion other) => ComparePrecedenceTo(other) == 0;

    /// <summary>
    /// Compares two versions by SemVer 2.0.0 precedence rules. Build metadata is ignored.
    /// </summary>
    internal static int ComparePrecedence(SemVersion? version1, SemVersion? version2)
    {
        if (ReferenceEquals(version1, version2))
        {
            return 0;
        }

        if (version1 is null)
        {
            return -1;
        }

        if (version2 is null)
        {
            return 1;
        }

        return version1.ComparePrecedenceTo(version2);
    }

    /// <summary>
    /// Compares this version to another by SemVer 2.0.0 precedence rules.
    /// Build metadata is ignored.
    /// </summary>
    internal int ComparePrecedenceTo(SemVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePrereleaseIdentifiers(_prereleaseIdentifiers, other._prereleaseIdentifiers);
    }

    /// <summary>
    /// Compares prerelease identifiers following SemVer 2.0.0 precedence:
    /// - No prerelease > has prerelease (1.0.0 > 1.0.0-alpha)
    /// - Numeric identifiers compared as integers
    /// - Alphanumeric identifiers compared lexically (ordinal)
    /// - Numeric &lt; alphanumeric
    /// - Fewer identifiers &lt; more identifiers when all preceding are equal
    /// </summary>
    private static int ComparePrereleaseIdentifiers(string[] left, string[] right)
    {
        var leftHas = left.Length > 0;
        var rightHas = right.Length > 0;

        if (!leftHas && !rightHas)
        {
            return 0;
        }

        // A version without prerelease has HIGHER precedence.
        if (!leftHas)
        {
            return 1;
        }

        if (!rightHas)
        {
            return -1;
        }

        var minLength = Math.Min(left.Length, right.Length);

        for (var i = 0; i < minLength; i++)
        {
            var cmp = CompareIdentifier(left[i], right[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNum = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNum);
        var rightIsNum = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNum);

        if (leftIsNum && rightIsNum)
        {
            return leftNum.CompareTo(rightNum);
        }

        // Numeric identifiers always have lower precedence than alphanumeric.
        if (leftIsNum)
        {
            return -1;
        }

        if (rightIsNum)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public int CompareTo(SemVersion? other) => ComparePrecedence(this, other);

    /// <inheritdoc/>
    public bool Equals(SemVersion? other)
    {
        if (other is null)
        {
            return false;
        }

        return Major == other.Major
            && Minor == other.Minor
            && Patch == other.Patch
            && string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal)
            && string.Equals(Metadata, other.Metadata, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SemVersion);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease, Metadata);

    public static bool operator ==(SemVersion? left, SemVersion? right) => Equals(left, right);
    public static bool operator !=(SemVersion? left, SemVersion? right) => !Equals(left, right);

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

        if (Prerelease.Length > 0)
        {
            sb.Append('-');
            sb.Append(Prerelease);
        }

        if (Metadata.Length > 0)
        {
            sb.Append('+');
            sb.Append(Metadata);
        }

        return sb.ToString();
    }

    private sealed class PrecedenceComparerImpl : IComparer<SemVersion>
    {
        public int Compare(SemVersion? x, SemVersion? y) => ComparePrecedence(x, y);
    }
}

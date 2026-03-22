// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Globalization;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public sealed class StringComparersTests
{
    [Fact]
    public void StringComparersAndStringComparisonsMatch()
    {
        var flags = BindingFlags.Public | BindingFlags.Static;

        var comparers = typeof(StringComparers).GetProperties(flags).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var comparisons = typeof(StringComparisons).GetProperties(flags).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        var currentCulture = CultureInfo.CurrentCulture;
        var currentUICulture = CultureInfo.CurrentUICulture;
        var defaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultThreadCurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            // Temporarily set the culture to en-AU to ensure consistent results.
            // This prevents test failures when the current culture is the invariant culture.
            CultureInfo.CurrentCulture = new CultureInfo("en-AU");
            CultureInfo.CurrentUICulture = new CultureInfo("en-AU");
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-AU");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-AU");

            ValidateSets();

            ValidateValues();
        }
        finally
        {
            CultureInfo.CurrentCulture = currentCulture;
            CultureInfo.CurrentUICulture = currentUICulture;
            CultureInfo.DefaultThreadCurrentCulture = defaultThreadCurrentCulture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultThreadCurrentUICulture;
        }

        void ValidateSets()
        {
            var comparerNames = comparers.Select(c => c.Name).ToList();
            var comparisonNames = comparisons.Select(c => c.Name).ToList();

            // Check that all comparers have a matching comparison.
            // Include details about which ones are missing in the test failure message to make fixing easier.
            var extraComparers = comparerNames.Except(comparisonNames, StringComparer.Ordinal).ToList();
            var extraComparisons = comparisonNames.Except(comparerNames, StringComparer.Ordinal).ToList();

            if (extraComparers.Count + extraComparisons.Count != 0)
            {
                Assert.Fail($"""
                Mismatched {nameof(StringComparers)} and {nameof(StringComparisons)}:
                - Comparers without matching comparisons: {string.Join(", ", extraComparers)}
                - Comparisons without matching comparers: {string.Join(", ", extraComparisons)}
                """);
            }
        }

        void ValidateValues()
        {
            var comparerValues = comparers.Select(c => (c.Name, Value: (StringComparer)c.GetValue(null)!)).ToList();
            var comparisonValues = comparisons.Select(c => (c.Name, Value: (StringComparison)c.GetValue(null)!)).ToList();

            // Check that all comparer values match the corresponding comparison values.
            foreach (var (comparer, comparison) in comparerValues.Zip(comparisonValues))
            {
                Assert.Equal(comparer.Name, comparison.Name, StringComparer.Ordinal);

                var comparerKind = GetComparerKind(comparer.Value);
                var comparisonKind = GetComparisonKind(comparison.Value);

                if (!string.Equals(comparerKind, comparisonKind, StringComparison.Ordinal))
                {
                    Assert.Fail($"""
                    Mismatched comparisons:
                    - {nameof(StringComparers)}.{comparer.Name} = {comparerKind}
                    - {nameof(StringComparisons)}.{comparer.Name} = {comparisonKind}
                    """);
                }
            }

            return;

            static string GetComparerKind(StringComparer comparer)
            {
                foreach (var (c, name) in Comparers())
                {
                    if (Equals(c, comparer))
                    {
                        return name;
                    }
                }

                Assert.Fail("Unknown comparer: " + comparer);
                return null!; // Unreachable

                static IEnumerable<(StringComparer, string)> Comparers()
                {
                    yield return (StringComparer.Ordinal, nameof(StringComparer.Ordinal));
                    yield return (StringComparer.OrdinalIgnoreCase, nameof(StringComparer.OrdinalIgnoreCase));
                    yield return (StringComparer.CurrentCulture, nameof(StringComparer.CurrentCulture));
                    yield return (StringComparer.CurrentCultureIgnoreCase, nameof(StringComparer.CurrentCultureIgnoreCase));
                    yield return (StringComparer.InvariantCulture, nameof(StringComparer.InvariantCulture));
                    yield return (StringComparer.InvariantCultureIgnoreCase, nameof(StringComparer.InvariantCultureIgnoreCase));
                }
            }

            static string GetComparisonKind(StringComparison comparison)
            {
                foreach (var (c, name) in Comparisons())
                {
                    if (c == comparison)
                    {
                        return name;
                    }
                }

                Assert.Fail("Unknown comparison: " + comparison);
                return null!; // Unreachable

                static IEnumerable<(StringComparison, string)> Comparisons()
                {
                    yield return (StringComparison.Ordinal, nameof(StringComparison.Ordinal));
                    yield return (StringComparison.OrdinalIgnoreCase, nameof(StringComparison.OrdinalIgnoreCase));
                    yield return (StringComparison.CurrentCulture, nameof(StringComparison.CurrentCulture));
                    yield return (StringComparison.CurrentCultureIgnoreCase, nameof(StringComparison.CurrentCultureIgnoreCase));
                    yield return (StringComparison.InvariantCulture, nameof(StringComparison.InvariantCulture));
                    yield return (StringComparison.InvariantCultureIgnoreCase, nameof(StringComparison.InvariantCultureIgnoreCase));
                }
            }
        }
    }
}

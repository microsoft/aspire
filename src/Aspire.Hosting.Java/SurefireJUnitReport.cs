// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Java;

/// <summary>
/// Reads standard, finalized Maven Surefire XML reports.
/// </summary>
internal static class SurefireJUnitReport
{
    internal static async Task CollectAsync(ResourceTestingContext context, string reportDirectory)
    {
        await context.WaitForCompletionAsync().ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();
        RejectLinkedDirectories(reportDirectory, reportDirectory);
        RejectLinkedDirectories(context.OutputPath, context.TestingContext.OutputPath);
        // Surefire owns this fresh invocation directory. Only its standard, top-level
        // TEST-<class>.xml files are reports; .txt and JVM dump files are not test cases.
        // https://maven.apache.org/surefire/maven-surefire-plugin/
        var paths = Directory.GetFiles(reportDirectory)
            .Where(path => Path.GetFileName(path).StartsWith("TEST-", StringComparison.Ordinal) &&
                path.EndsWith(".xml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidDataException("Maven Surefire produced no TEST-*.xml reports. Configure reportsDirectory from " +
                "${env.ASPIRE_JUNIT_REPORT_DIRECTORY} in an environment-activated POM profile. Inspect resource logs for build or no-test failures.");
        }
        var results = new List<TestResult>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("JUnit reports must not be symbolic links or reparse points.");
            }
            var destination = Path.Combine(context.OutputPath, Path.GetFileName(path));
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, context.CancellationToken).ConfigureAwait(false);
                }
            }
            var stream = File.OpenRead(destination);
            await using var _ = stream.ConfigureAwait(false);
            foreach (var result in await ReadAsync(stream, context.CancellationToken).ConfigureAwait(false))
            {
                if (!identities.Add(result.Name))
                {
                    throw new InvalidDataException($"Duplicate JUnit test identity '{result.Name}'. Retries and overlapping reports are unsupported.");
                }
                results.Add(result);
            }
        }
        // Validate the entire report set before publishing, so a later duplicate or corrupt
        // suite cannot leave an apparently complete partial set in the common summary.
        foreach (var result in results)
        {
            context.PublishResult(result);
        }
    }

    internal static async Task<IReadOnlyList<TestResult>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        if (document.Root is not { } suite || suite.Name != "testsuite")
        {
            throw new InvalidDataException("A Surefire report must contain one unnamespaced testsuite root.");
        }
        RequiredAttribute(suite, "name");
        var results = new List<TestResult>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in suite.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name == "properties" || entry.Name == "system-out" || entry.Name == "system-err")
            {
                continue;
            }
            if (entry.Name != "testcase" || entry.Elements().Any(child =>
                child.Name != "failure" && child.Name != "error" && child.Name != "skipped" &&
                child.Name != "system-out" && child.Name != "system-err"))
            {
                throw new InvalidDataException("Unsupported Surefire entry or outcome (including retries).");
            }
            // Standard Surefire XML: <testcase name="probe(String)[1]" classname="demo.ApiTest"
            // time="0.125"><failure type="..." message="...">stack trace</failure></testcase>.
            // Preserve parameterized display names literally; do not infer IDs or richer
            // timeout/cancellation outcomes from Java exception text.
            var name = RequiredAttribute(entry, "classname") + "." + RequiredAttribute(entry, "name");
            if (!identities.Add(name))
            {
                throw new InvalidDataException($"Duplicate JUnit test identity '{name}'.");
            }
            var outcomes = entry.Elements().Where(child =>
                child.Name == "failure" || child.Name == "error" || child.Name == "skipped").ToArray();
            if (outcomes.Length > 1)
            {
                throw new InvalidDataException("A Surefire test case must have at most one outcome.");
            }
            var outcome = outcomes.SingleOrDefault();
            var status = outcome?.Name.LocalName switch
            {
                null => TestOutcome.Passed,
                "failure" => TestOutcome.Failed,
                "error" => TestOutcome.Error,
                "skipped" => TestOutcome.Skipped,
                _ => throw new InvalidDataException("Unsupported JUnit outcome.")
            };
            TimeSpan? duration = null;
            if (entry.Attribute("time") is { } time)
            {
                if (!double.TryParse(time.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                    !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                {
                    throw new InvalidDataException($"Invalid JUnit duration '{time.Value}'.");
                }
                duration = TimeSpan.FromSeconds(seconds);
            }
            var messages = new[]
            {
                outcome is null ? null : $"{outcome.Name.LocalName}: {outcome.Attribute("type")?.Value} {outcome.Attribute("message")?.Value}".TrimEnd(),
                outcome?.Value, entry.Element("system-out")?.Value, entry.Element("system-err")?.Value,
                suite.Element("system-out")?.Value, suite.Element("system-err")?.Value
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            results.Add(new TestResult
            {
                Name = name,
                Outcome = status,
                Duration = duration,
                Message = messages.Length == 0 ? null : string.Join(Environment.NewLine, messages)
            });
        }
        if (Count(suite, "tests") != results.Count ||
            Count(suite, "failures") != results.Count(result => result.Outcome == TestOutcome.Failed) ||
            Count(suite, "errors") != results.Count(result => result.Outcome == TestOutcome.Error) ||
            Count(suite, "skipped") != results.Count(result => result.Outcome == TestOutcome.Skipped))
        {
            throw new InvalidDataException("Surefire suite counters are inconsistent with its test cases.");
        }

        return results;
    }

    private static int Count(XElement element, string name)
    {
        if (!int.TryParse(RequiredAttribute(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            throw new InvalidDataException($"Invalid Surefire suite counter '{name}'.");
        }

        return count;
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing JUnit attribute '{name}' on '{element.Name}'.");
        }

        return value;
    }

    private static void RejectLinkedDirectories(string path, string root)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("JUnit report directories must not contain symbolic links or reparse points.");
            }
            if (directory.FullName == root)
            {
                break;
            }
        }
    }
}

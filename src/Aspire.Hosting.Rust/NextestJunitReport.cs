// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Rust;

/// <summary>
/// Reads finalized nextest JUnit reports.
/// </summary>
internal static class NextestJunitReport
{
    internal const string FileName = "junit.xml";

    internal static async Task CollectAsync(ResourceTestingContext context)
    {
        // Capture waits for authoritative completion and drains diagnostics even after tool/build failure.
        // A failed process must not prevent harvesting a valid final report.
        await context.CaptureOutputAsync().ConfigureAwait(false);
        ValidateDirectory(context.OutputPath, context.RunContext.OutputPath);
        var path = Path.Combine(context.OutputPath, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Nextest did not produce junit.xml. Requires cargo-nextest >= 0.9.143. " +
                "Build, discovery, no-tests, startup, or interrupted execution may not produce a final report; inspect stdout.log and stderr.log.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The nextest report must not be a symbolic link or reparse point.");
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var _ = stream.ConfigureAwait(false);
        var results = await ReadAsync(stream, context.CancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            context.PublishResult(result);
        }
    }

    internal static void ValidateDirectory(string outputPath, string runPath)
    {
        for (var directory = new DirectoryInfo(outputPath); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The nextest output directory must not contain symbolic links or reparse points.");
            }
            if (directory.FullName == runPath)
            {
                return;
            }
        }
        throw new InvalidDataException("The nextest output directory must be inside the assigned test run directory.");
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
        if (document.Root is not { } root || root.Name != "testsuites")
        {
            throw new InvalidDataException("The nextest report must contain a JUnit testsuites root.");
        }
        var results = new List<TestResult>();
        var totalFailures = 0;
        var totalErrors = 0;
        var totalSkipped = 0;
        foreach (var suite in root.Elements())
        {
            if (suite.Name != "testsuite")
            {
                throw new InvalidDataException("Only flat nextest test binary suites are supported.");
            }
            var suiteName = RequiredAttribute(suite, "name");
            var suiteResults = new List<TestResult>();
            var failures = 0;
            var errors = 0;
            var skipped = 0;
            foreach (var entry in suite.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name == "properties" || entry.Name == "system-out" || entry.Name == "system-err")
                {
                    continue;
                }
                if (entry.Name != "testcase" || entry.Elements().Any(child =>
                    child.Name != "failure" && child.Name != "error" && child.Name != "skipped" &&
                    child.Name != "system-out" && child.Name != "system-err" && child.Name != "properties"))
                {
                    throw new InvalidDataException("The nextest report contains an unsupported entry, retry, or outcome.");
                }
                // Nextest writes one suite per binary and one case per test:
                // <testsuite name="crate::integration"><testcase classname="crate::integration"
                // name="module::test" time="0.001"><failure type="test failure">...</failure></testcase>
                // Retry/flaky elements are deliberately rejected: this adapter runs each test once.
                // https://nexte.st/docs/machine-readable/junit/
                var outcomes = entry.Elements().Where(child =>
                    child.Name == "failure" || child.Name == "error" || child.Name == "skipped").ToArray();
                if (outcomes.Length > 1)
                {
                    throw new InvalidDataException("A nextest test case must contain at most one final outcome.");
                }
                var outcome = outcomes.SingleOrDefault();
                var status = TestOutcome.Passed;
                if (outcome?.Name == "failure")
                {
                    failures++;
                    status = TestOutcome.Failed;
                }
                else if (outcome?.Name == "error")
                {
                    errors++;
                    status = TestOutcome.Failed;
                }
                else if (outcome?.Name == "skipped")
                {
                    skipped++;
                    status = TestOutcome.Skipped;
                }
                TimeSpan? duration = null;
                if (entry.Attribute("time") is { } time)
                {
                    if (!double.TryParse(time.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                        !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                    {
                        throw new InvalidDataException($"Invalid nextest duration '{time.Value}'.");
                    }
                    duration = TimeSpan.FromSeconds(seconds);
                }
                var messages = new[]
                {
                    outcome is null ? null : $"{outcome.Name.LocalName}: {outcome.Attribute("type")?.Value} {outcome.Attribute("message")?.Value}".TrimEnd(),
                    outcome?.Value, entry.Element("system-out")?.Value, entry.Element("system-err")?.Value
                }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                suiteResults.Add(new TestResult
                {
                    Name = suiteName + "/" + RequiredAttribute(entry, "name"),
                    Outcome = status,
                    Duration = duration,
                    Message = messages.Length == 0 ? null : string.Join(Environment.NewLine, messages)
                });
            }
            if (ReadCount(suite, "tests") != suiteResults.Count || ReadCount(suite, "failures") != failures ||
                ReadCount(suite, "errors") != errors || ReadOptionalCount(suite, "skipped") != skipped)
            {
                throw new InvalidDataException("The nextest suite counters are inconsistent with its reported outcomes.");
            }
            results.AddRange(suiteResults);
            totalFailures += failures;
            totalErrors += errors;
            totalSkipped += skipped;
        }
        if (results.Count == 0 || ReadCount(root, "tests") != results.Count ||
            ReadCount(root, "failures") != totalFailures || ReadCount(root, "errors") != totalErrors ||
            ReadOptionalCount(root, "skipped") != totalSkipped)
        {
            throw new InvalidDataException("The nextest report is empty or its totals are inconsistent.");
        }

        return results;
    }

    private static int ReadOptionalCount(XElement element, string name)
    {
        return element.Attribute(name) is null ? 0 : ReadCount(element, name);
    }

    private static int ReadCount(XElement element, string name)
    {
        if (!int.TryParse(RequiredAttribute(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"Invalid nextest JUnit counter '{name}'.");
        }

        return value;
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing nextest JUnit attribute '{name}' on '{element.Name.LocalName}'.");
        }

        return value;
    }
}

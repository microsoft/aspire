// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001 // Translates pytest reports into the experimental testing contract.

namespace Aspire.Hosting.Python;

/// <summary>
/// Reads finalized, built-in pytest JUnit reports without merging or deduplicating outcomes.
/// </summary>
internal static class PytestJunitReport
{
    internal const string FileName = "pytest.xml";

    internal static async Task CollectAsync(ResourceTestingContext context)
    {
        // Process failure does not preclude a useful report. Only Aspire cancellation interrupts harvesting.
        await context.WaitForCompletionAsync().ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();
        for (var directory = new DirectoryInfo(context.OutputPath); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The pytest report directory must not contain symbolic links or reparse points.");
            }
            if (directory.FullName == context.RunContext.OutputPath)
            {
                break;
            }
        }
        var path = Path.Combine(context.OutputPath, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Pytest did not produce pytest.xml. Declare pytest in the selected " +
                "virtual environment and keep its built-in JUnit plugin enabled. Inspect the resource logs for errors.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The pytest report must not be a symbolic link or reparse point.");
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var _ = stream.ConfigureAwait(false);
        var results = await ReadAsync(stream, context.CancellationToken).ConfigureAwait(false);
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
        if (document.Root is not { } root || root.Name != "testsuites" && root.Name != "testsuite")
        {
            throw new InvalidDataException("The pytest report must contain a JUnit testsuites or testsuite root.");
        }
        var suites = root.Name == "testsuite" ? new[] { root } : root.Elements().ToArray();
        if (suites.Length == 0 || suites.Any(suite => suite.Name != "testsuite" || suite.Elements("testsuite").Any()))
        {
            throw new InvalidDataException("Only flat pytest JUnit test suites are supported.");
        }
        var results = new List<TestResult>();
        foreach (var suite in suites)
        {
            var initialCount = results.Count;
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
                    child.Name != "properties" && child.Name != "system-out" && child.Name != "system-err"))
                {
                    throw new InvalidDataException("The pytest report contains an unsupported JUnit entry or outcome.");
                }
                var name = RequiredAttribute(entry, "name");
                if (entry.Attribute("classname")?.Value is { Length: > 0 } className)
                {
                    name = className + "." + name;
                }
                TimeSpan? duration = null;
                if (entry.Attribute("time") is { } time)
                {
                    if (!double.TryParse(time.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                        !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                    {
                        throw new InvalidDataException($"Invalid pytest duration '{time.Value}'.");
                    }
                    duration = TimeSpan.FromSeconds(seconds);
                }
                // Built-in pytest emits:
                // <testcase classname="test_api" name="test_value[1]" time="0.001"><failure ...>...</failure></testcase>
                // A call failure followed by a teardown error can produce TWO cases with identical
                // names; a skip plus teardown error can share one case. Preserve every outcome rather
                // than inferring unique node IDs, choosing a winner, or subtracting suite counters.
                // https://github.com/pytest-dev/pytest/blob/main/src/_pytest/junitxml.py
                var outcomes = entry.Elements().Where(child =>
                    child.Name == "failure" || child.Name == "error" || child.Name == "skipped").ToArray();
                foreach (var outcome in outcomes.Length == 0 ? new XElement?[] { null } : outcomes)
                {
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
                    var messages = new[]
                    {
                        outcome is null ? null : $"{outcome.Name.LocalName}: {outcome.Attribute("type")?.Value} {outcome.Attribute("message")?.Value}".TrimEnd(),
                        outcome?.Value, entry.Element("system-out")?.Value, entry.Element("system-err")?.Value
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                    results.Add(new TestResult
                    {
                        Name = name,
                        Outcome = status,
                        Duration = duration,
                        Message = messages.Length == 0 ? null : string.Join(Environment.NewLine, messages)
                    });
                }
            }
            var tests = ReadCount(suite, "tests");
            // Older pytest versions count a passing call and its teardown error separately in
            // 'tests', but emit just one testcase containing the error (pytest #3850). The XML
            // does not identify the hidden pass. Keep raw totals in the artifact, and publish only
            // observed outcomes; do not invent passes to reconcile this counter.
            if (ReadCount(suite, "failures") != failures || ReadCount(suite, "errors") != errors ||
                ReadCount(suite, "skipped") != skipped || tests > (long)results.Count - initialCount + errors ||
                tests == 0 && results.Count != initialCount)
            {
                throw new InvalidDataException("The pytest JUnit suite counters are inconsistent with its reported outcomes.");
            }
        }

        return results;
    }

    private static int ReadCount(XElement element, string name)
    {
        if (!int.TryParse(RequiredAttribute(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"Invalid pytest JUnit counter '{name}'.");
        }

        return value;
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing pytest JUnit attribute '{name}' on '{element.Name.LocalName}'.");
        }

        return value;
    }
}

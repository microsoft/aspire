// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;

namespace Infrastructure.Tests;

/// <summary>
/// Creates test .trx files and zipped artifacts for tool tests.
/// </summary>
public static class TestTrxBuilder
{
    private static readonly XNamespace s_namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static string CreateTrxFile(string outputPath, params TestTrxCase[] testCases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var document = new XDocument(
            new XElement(
                s_namespace + "TestRun",
                new XElement(
                    s_namespace + "TestDefinitions",
                    testCases.Select(CreateTestDefinitionElement)),
                new XElement(
                    s_namespace + "Results",
                    testCases.Select(CreateResultElement)),
                new XElement(
                    s_namespace + "ResultSummary",
                    new XElement(
                        s_namespace + "Counters",
                        new XAttribute("total", testCases.Length),
                        new XAttribute("executed", testCases.Length),
                        new XAttribute("passed", testCases.Count(test => test.Outcome == "Passed")),
                        new XAttribute("failed", testCases.Count(test => test.Outcome == "Failed")),
                        new XAttribute("error", testCases.Count(test => test.Outcome == "Error")),
                        new XAttribute("timeout", testCases.Count(test => test.Outcome == "Timeout")),
                        new XAttribute("aborted", 0),
                        new XAttribute("inconclusive", 0),
                        new XAttribute("passedButRunAborted", 0),
                        new XAttribute("notRunnable", 0),
                        new XAttribute("notExecuted", 0)))));

        document.Save(outputPath);
        return outputPath;
    }

    public static string CreateArtifactZip(string outputPath, string trxEntryPath, params TestTrxCase[] testCases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trxEntryPath);

        var tempDirectory = Directory.CreateTempSubdirectory("aspire-test-trx").FullName;

        try
        {
            var trxPath = Path.Combine(tempDirectory, Path.GetFileName(trxEntryPath));
            CreateTrxFile(trxPath, testCases);

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(trxPath, trxEntryPath);
            return outputPath;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static XElement CreateTestDefinitionElement(TestTrxCase testCase, int index)
    {
        var lastDotIndex = testCase.CanonicalTestName.LastIndexOf('.');
        if (lastDotIndex < 0)
        {
            throw new InvalidOperationException($"Canonical test name '{testCase.CanonicalTestName}' must contain at least one '.'.");
        }

        return new XElement(
            s_namespace + "UnitTest",
            new XAttribute("name", testCase.DisplayName),
            new XAttribute("id", $"test-{index}"),
            new XElement(
                s_namespace + "TestMethod",
                new XAttribute("className", testCase.CanonicalTestName[..lastDotIndex]),
                new XAttribute("name", testCase.TestMethodName ?? testCase.CanonicalTestName[(lastDotIndex + 1)..])));
    }

    private static XElement CreateResultElement(TestTrxCase testCase, int index)
    {
        return new XElement(
            s_namespace + "UnitTestResult",
            new XAttribute("testId", $"test-{index}"),
            new XAttribute("testName", testCase.DisplayName),
            new XAttribute("outcome", testCase.Outcome),
            new XElement(
                s_namespace + "Output",
                CreateErrorInfoElement(testCase),
                string.IsNullOrEmpty(testCase.StdOut) ? null : new XElement(s_namespace + "StdOut", testCase.StdOut)));
    }

    private static XElement? CreateErrorInfoElement(TestTrxCase testCase)
    {
        if (string.IsNullOrEmpty(testCase.ErrorMessage) && string.IsNullOrEmpty(testCase.StackTrace))
        {
            return null;
        }

        return new XElement(
            s_namespace + "ErrorInfo",
            string.IsNullOrEmpty(testCase.ErrorMessage) ? null : new XElement(s_namespace + "Message", testCase.ErrorMessage),
            string.IsNullOrEmpty(testCase.StackTrace) ? null : new XElement(s_namespace + "StackTrace", testCase.StackTrace));
    }
}

public sealed record TestTrxCase(
    string CanonicalTestName,
    string DisplayName,
    string Outcome,
    string ErrorMessage = "",
    string StackTrace = "",
    string StdOut = "",
    string? TestMethodName = null);

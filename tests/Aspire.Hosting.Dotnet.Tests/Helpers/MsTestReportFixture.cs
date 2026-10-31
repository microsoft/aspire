// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Hosting.Dotnet.Tests.Helpers;

internal static class MsTestReportFixture
{
    internal static XNamespace Namespace { get; } = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static XDocument Create(params string[] outcomes)
    {
        var testId = Guid.NewGuid();
        var results = new XElement(Namespace + "Results");
        foreach (var outcome in outcomes)
        {
            results.Add(new XElement(Namespace + "UnitTestResult",
                new XAttribute("executionId", Guid.NewGuid()),
                new XAttribute("testId", testId),
                new XAttribute("testName", "data row (1 & 2)"),
                new XAttribute("outcome", outcome),
                new XAttribute("duration", "00:00:01.2345678")));
        }

        return new XDocument(new XElement(Namespace + "TestRun",
            new XAttribute("id", Guid.NewGuid()),
            results,
            new XElement(Namespace + "TestDefinitions",
                new XElement(Namespace + "UnitTest", new XAttribute("id", testId),
                    new XElement(Namespace + "TestMethod",
                        new XAttribute("className", "Example.Tests"), new XAttribute("name", "DataRow")))),
            new XElement(Namespace + "ResultSummary", new XAttribute("outcome", outcomes.Contains("Failed") ? "Failed" : "Completed"),
                new XElement(Namespace + "Counters",
                    new XAttribute("total", outcomes.Length),
                    new XAttribute("passed", outcomes.Count(o => o == "Passed")),
                    new XAttribute("failed", outcomes.Count(o => o == "Failed")),
                    new XAttribute("timeout", 0),
                    new XAttribute("notExecuted", outcomes.Count(o => o == "NotExecuted"))))));
    }
}

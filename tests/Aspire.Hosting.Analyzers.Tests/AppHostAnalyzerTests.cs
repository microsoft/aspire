// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.Analyzers.Tests;

public class AppHostAnalyzerTests
{
    [Fact]
    public async Task MissingAspireHostingReference_DoesNotThrow()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>(
            """
            System.Console.WriteLine("test");

            public static class TestExtensions
            {
                public static void AddMultipleParameters(string param1Name, string param2Name)
                {
                }
            }
            """,
            [],
            includeAspireHostingReference: false);

        await test.RunAsync();
    }
}

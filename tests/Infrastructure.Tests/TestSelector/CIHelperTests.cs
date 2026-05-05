// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector;
using Xunit;

namespace Infrastructure.Tests.TestSelector;

public class CIHelperTests
{
    [Fact]
    public void WriteWarning_GitHub_WritesGitHubWarningAnnotation()
    {
        using var writer = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(writer);

        try
        {
            CIHelper.WriteWarning("GitHub", "fallback warning");
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal($"::warning::fallback warning{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void WriteWarning_Local_WritesPlainWarning()
    {
        using var writer = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(writer);

        try
        {
            CIHelper.WriteWarning("Local", "fallback warning");
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal($"Warning: fallback warning{Environment.NewLine}", writer.ToString());
    }
}

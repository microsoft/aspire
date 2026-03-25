// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Python.Tests;

public class PythonLanguageSupportTests
{
    [Fact]
    public void Scaffold_GeneratesAppHostWithoutModulesPathBootstrap()
    {
        var languageSupport = new PythonLanguageSupport();

        var files = languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = "/tmp/apphost",
            ProjectName = "AppHost"
        });

        var appHost = files["apphost.py"];

        Assert.Contains("from aspire_app import create_builder", appHost);
        Assert.DoesNotContain("sys.path.insert", appHost);
        Assert.DoesNotContain("from pathlib import Path", appHost);
        Assert.DoesNotContain("import sys", appHost);
    }
}

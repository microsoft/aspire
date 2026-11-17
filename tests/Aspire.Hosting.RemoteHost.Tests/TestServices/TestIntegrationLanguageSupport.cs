// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost.Tests;

internal sealed class TestIntegrationLanguageSupport : ILanguageSupport
{
    private readonly string _command = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the test executable.");

    public TestIntegrationLanguageSupport()
    {
    }

    public string Language => "test/launch";
    public Dictionary<string, string> Scaffold(ScaffoldRequest request) => throw new NotSupportedException();
    public DetectionResult Detect(string directoryPath) => throw new NotSupportedException();
    public RuntimeSpec GetRuntimeSpec() => throw new NotSupportedException();

    public JsonElement GetIntegrationHostSpec() => JsonSerializer.SerializeToElement(new
    {
        execute = new
        {
            command = _command,
            args = new[] { "{entryPoint}", "space argument" }
        }
    });
}

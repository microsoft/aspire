// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Xunit.Sdk;

namespace Aspire.Templates.Tests;

/// <summary>
/// This fixture runs a project created from a given template
/// </summary>
public class TemplateAppFixture : IAsyncLifetime
{
    private readonly IMessageSink _diagnosticMessageSink;
    private readonly TestOutputWrapper _testOutput;
    private readonly string _dotnetNewArgs;
    private readonly string _config;
    private readonly TestTargetFramework? _tfm;

    public AspireProject? Project { get; private set; }

    public string Id { get; init; }
    public string TemplateName { get; set; }

    public TemplateAppFixture(IMessageSink diagnosticMessageSink, string templateName, string? dotnetNewArgs = null, string config = "Debug", TestTargetFramework tfm = TestTargetFramework.Net9)
    {
        _diagnosticMessageSink = diagnosticMessageSink;
        _testOutput = new TestOutputWrapper(messageSink: _diagnosticMessageSink);
        _dotnetNewArgs = dotnetNewArgs ?? string.Empty;
        _config = config;
        Id = TemplateTestsBase.GetNewProjectId(prefix: $"{templateName}_{tfm.ToTFMString()}");
        TemplateName = templateName;
        _tfm = tfm;
    }

    public async ValueTask InitializeAsync()
    {
        Project = await AspireProject.CreateNewTemplateProjectAsync(
            Id,
            TemplateName,
            _testOutput,
            extraArgs: _dotnetNewArgs,
            buildEnvironment: BuildEnvironment.ForDefaultFramework,
            targetFramework: _tfm);

        await Project.BuildAsync(extraBuildArgs: [$"-c {_config}"]);
        await Project.StartAppHostAsync(extraArgs: [$"-c {_config}"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Project is not null)
        {
            await Project.DisposeAsync();
        }
    }

    public void EnsureAppHostRunning()
    {
        if (Project!.AppHostProcess is null || Project.AppHostProcess.HasExited || Project.AppExited?.Task.IsCompleted == true)
        {
            throw new InvalidOperationException($"The app host process is not running. {Project.AppHostProcess?.HasExited}, {Project.AppExited?.Task.IsCompleted}");
        }
    }
}

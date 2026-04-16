// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using System.Reflection;
using Xunit;
using Xunit.v3;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// When applied to a test method, captures directories to
/// <c>testresults/workspaces/{testName}/</c> on test failure so the generated
/// files are uploaded as CI artifacts for debugging.
/// <para>
/// Register paths to capture via <c>TestContext.Current.KeyValueStorage</c>:
/// <list type="bullet">
/// <item><c>"WorkspacePath"</c> — the primary workspace directory</item>
/// <item><c>"CapturePath:{label}"</c> — additional directories to capture under the given label</item>
/// <item><c>"CaptureFile:{fileName}"</c> — additional files to capture under the given destination name</item>
/// </list>
/// Workspace capture is automatic when using <see cref="Aspire.Cli.Tests.Utils.TemporaryWorkspace.Create"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class CaptureWorkspaceOnFailureAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        _ = methodUnderTest;
        _ = test;

        TestContext.Current?.KeyValueStorage["PreserveWorkspaceOnFailure"] = true;
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (TestContext.Current.TestState?.Result is not TestResult.Failed)
        {
            if (!CliE2ETestHelpers.IsRunningInCI &&
                TestContext.Current.KeyValueStorage.TryGetValue("WorkspacePath", out var workspaceValue) &&
                workspaceValue is string preservedWorkspacePath)
            {
                TemporaryWorkspace.ReleasePreservation(preservedWorkspacePath);
            }

            return;
        }

        var testName = $"{test.TestCase.TestClassName}.{methodUnderTest.Name}";

        try
        {
            if (!CliE2ETestHelpers.IsRunningInCI)
            {
                if (TestContext.Current.KeyValueStorage.TryGetValue("WorkspacePath", out var workspaceValue) &&
                    workspaceValue is string localWorkspacePath)
                {
                    Console.WriteLine($"Failed test workspace preserved at: {localWorkspacePath}");
                }

                foreach (var kvp in TestContext.Current.KeyValueStorage)
                {
                    if (kvp.Key.StartsWith("CapturePath:", StringComparison.Ordinal) &&
                        kvp.Value is string path &&
                        Directory.Exists(path))
                    {
                        var label = kvp.Key["CapturePath:".Length..];
                        Console.WriteLine($"Failed test diagnostics '{label}' available at: {path}");
                    }

                    if (kvp.Key.StartsWith("CaptureFile:", StringComparison.Ordinal) &&
                        kvp.Value is string filePath &&
                        File.Exists(filePath))
                    {
                        var fileName = kvp.Key["CaptureFile:".Length..];
                        Console.WriteLine($"Failed test file '{fileName}' available at: {filePath}");
                    }
                }

                return;
            }

            // Capture primary workspace
            if (TestContext.Current.KeyValueStorage.TryGetValue("WorkspacePath", out var value) &&
                value is string workspacePath &&
                Directory.Exists(workspacePath))
            {
                var capturePath = CliE2ETestHelpers.CaptureDirectory(workspacePath, testName, label: null);
                Console.WriteLine($"Captured failed test workspace to: {capturePath}");
            }

            // Capture additional registered paths (e.g., "CapturePath:aspire-home" → ~/.aspire)
            foreach (var kvp in TestContext.Current.KeyValueStorage)
            {
                if (kvp.Key.StartsWith("CapturePath:", StringComparison.Ordinal) &&
                    kvp.Value is string path &&
                    Directory.Exists(path))
                {
                    var label = kvp.Key["CapturePath:".Length..];
                    var capturePath = CliE2ETestHelpers.CaptureDirectory(path, testName, label);
                    Console.WriteLine($"Captured failed test diagnostics '{label}' to: {capturePath}");
                }

                if (kvp.Key.StartsWith("CaptureFile:", StringComparison.Ordinal) &&
                    kvp.Value is string filePath &&
                    File.Exists(filePath))
                {
                    var fileName = kvp.Key["CaptureFile:".Length..];
                    var capturePath = CliE2ETestHelpers.CaptureFile(filePath, testName, fileName);
                    Console.WriteLine($"Captured failed test file '{fileName}' to: {capturePath}");
                }
            }
        }
        catch
        {
            // Don't fail the test because of capture issues.
        }
    }
}

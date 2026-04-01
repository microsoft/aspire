// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
/// </list>
/// Workspace capture is automatic when using <see cref="Aspire.Cli.Tests.Utils.TemporaryWorkspace.Create"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class CaptureWorkspaceOnFailureAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (TestContext.Current.TestState?.Result is not TestResult.Failed)
        {
            return;
        }

        var testName = $"{test.TestCase.TestClassName}.{methodUnderTest.Name}";

        try
        {
            // Capture primary workspace
            if (TestContext.Current.KeyValueStorage.TryGetValue("WorkspacePath", out var value) &&
                value is string workspacePath &&
                Directory.Exists(workspacePath))
            {
                CliE2ETestHelpers.CaptureDirectory(workspacePath, testName, label: null);
            }

            // Capture additional registered paths (e.g., "CapturePath:aspire-home" → ~/.aspire)
            foreach (var kvp in TestContext.Current.KeyValueStorage)
            {
                if (kvp.Key.StartsWith("CapturePath:", StringComparison.Ordinal) &&
                    kvp.Value is string path &&
                    Directory.Exists(path))
                {
                    var label = kvp.Key["CapturePath:".Length..];
                    CliE2ETestHelpers.CaptureDirectory(path, testName, label);
                }
            }
        }
        catch
        {
            // Don't fail the test because of capture issues.
        }
    }
}

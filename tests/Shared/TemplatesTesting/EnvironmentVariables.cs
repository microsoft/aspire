// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;

namespace Aspire.Templates.Tests;

public static class EnvironmentVariables
{
    public static readonly string? SdkForTemplateTestingPath       = Environment.GetEnvironmentVariable("SDK_FOR_TEMPLATES_TESTING_PATH");
    public static readonly string? TestLogPath                     = Environment.GetEnvironmentVariable("TEST_LOG_PATH");
    public static readonly string? SkipProjectCleanup              = Environment.GetEnvironmentVariable("SKIP_PROJECT_CLEANUP");
    public static readonly string? BuiltNuGetsPath                 = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");
    public static readonly bool    ShowBuildOutput                 = Environment.GetEnvironmentVariable("SHOW_BUILD_OUTPUT") is "true";
    public static readonly bool    IsRunningOnCI                   = PlatformDetection.IsRunningOnCI;
    public static readonly bool    TestsRunningOutsideOfRepo       = Environment.GetEnvironmentVariable("TestsRunningOutsideOfRepo") is "true";
    public static readonly string? TestScenario                    = Environment.GetEnvironmentVariable("TEST_SCENARIO");
    public static readonly string? DefaultTFMForTesting            = Environment.GetEnvironmentVariable("DEFAULT_TFM_FOR_TESTING");
    public static readonly string? TestRootPath                    = Environment.GetEnvironmentVariable("DEV_TEMP");
    public static readonly bool    ConditionalSelectionRunOnlyBasicBuildTemplateScenarios = Environment.GetEnvironmentVariable("ConditionalSelectionRunOnlyBasicBuildTemplateScenarios") is "true";
    public static readonly bool    RunOnlyBasicBuildTemplatesTests = Environment.GetEnvironmentVariable("RunOnlyBasicBuildTemplateTests") is "true"
        || ConditionalSelectionRunOnlyBasicBuildTemplateScenarios;
}

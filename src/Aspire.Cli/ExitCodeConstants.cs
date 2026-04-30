// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

internal static class ExitCodeConstants
{
    public const int Success = 0;
    public const int InvalidCommand = 1;
    public const int FailedToDotnetRunAppHost = 2;
    public const int FailedToInstallTemplates = 3;
    public const int FailedToCreateNewProject = 4;
    public const int FailedToAddPackage = 5;
    public const int FailedToBuildArtifacts = 6;
    public const int FailedToFindProject = 7;
    public const int FailedToTrustCertificates = 8;
    public const int AppHostIncompatible = 9;
    public const int ConfigNotFound = 10;
    public const int SdkNotInstalled = 11;
    public const int DashboardFailure = 12;
    public const int FailedToUpgradeProject = 13;
    public const int CentralPackageManagementNotSupported = 14;
    public const int SingleFileAppHostNotSupported = 15;
    public const int FailedToExecuteResourceCommand = 16;
    public const int WaitTimeout = 17;
    public const int WaitResourceFailed = 18;
    public const int FailedToLoadConfiguration = 19;
    public const int FailedToStartCli = 20;
    public const int MissingRequiredArgument = 21;
}

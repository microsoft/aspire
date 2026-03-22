// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Contains constants for telemetry tag names and event names used by the Aspire CLI.
/// </summary>
internal static class TelemetryConstants
{
    /// <summary>
    /// Tag names for telemetry data.
    /// </summary>
    internal static class Tags
    {
        /// <summary>
        /// Tag for the hashed MAC address of the machine.
        /// </summary>
        public const string MacAddressHash = "machine.mac_address_hash";

        /// <summary>
        /// Tag for the unique device identifier.
        /// </summary>
        public const string DeviceId = "machine.device_id";

        /// <summary>
        /// Tag for the exception type.
        /// </summary>
        public const string ExceptionType = "exception.type";

        /// <summary>
        /// Tag for the exception message.
        /// </summary>
        public const string ExceptionMessage = "exception.message";

        /// <summary>
        /// Tag for the exception stack trace.
        /// </summary>
        public const string ExceptionStackTrace = "exception.stacktrace";

        /// <summary>
        /// Tag for the process ID.
        /// </summary>
        public const string ProcessPid = "process.pid";

        /// <summary>
        /// Tag for the process executable name.
        /// </summary>
        public const string ProcessExecutableName = "process.executable.name";

        /// <summary>
        /// Tag for the process exit code.
        /// </summary>
        public const string ProcessExitCode = "process.exit.code";

        /// <summary>
        /// Tag for the CLI command name.
        /// </summary>
        public const string CommandName = "aspire.cli.command.name";

        /// <summary>
        /// Tag for the CLI version.
        /// </summary>
        public const string CliVersion = "aspire.cli.version";

        /// <summary>
        /// Tag for the CLI build identifier, such as the file version or build ID.
        /// </summary>
        public const string CliBuildId = "aspire.cli.build_id";

        /// <summary>
        /// Tag for the deployment environment name ("ci" or "local").
        /// </summary>
        public const string DeploymentEnvironmentName = "deployment.environment.name";

        /// <summary>
        /// Tag for the detected SDK version.
        /// </summary>
        public const string SdkDetectedVersion = "aspire.cli.sdk.detected_version";

        /// <summary>
        /// Tag for the minimum required SDK version.
        /// </summary>
        public const string SdkMinimumRequiredVersion = "aspire.cli.sdk.minimum_required_version";

        /// <summary>
        /// Tag indicating the result of the SDK check operation.
        /// </summary>
        public const string SdkCheckResult = "aspire.cli.sdk.check_result";

        /// <summary>
        /// Tag for the operating system name.
        /// </summary>
        public const string OsName = "os.name";

        /// <summary>
        /// Tag for the operating system type.
        /// </summary>
        public const string OsType = "os.type";

        /// <summary>
        /// Tag for the operating system version.
        /// </summary>
        public const string OsVersion = "os.version";
    }

    /// <summary>
    /// Activity names for telemetry.
    /// </summary>
    internal static class Activities
    {
        /// <summary>
        /// Activity name for the main CLI entry point.
        /// </summary>
        public const string Main = "aspire/cli/main";

        /// <summary>
        /// Activity name for ensuring the SDK is installed.
        /// </summary>
        public const string EnsureSdkInstalled = "aspire/cli/ensure_sdk_installed";
    }

    /// <summary>
    /// Event names for telemetry activities.
    /// </summary>
    internal static class Events
    {
        /// <summary>
        /// Event name for recording errors in the CLI.
        /// </summary>
        public const string Error = "aspire/cli/error";
    }
}

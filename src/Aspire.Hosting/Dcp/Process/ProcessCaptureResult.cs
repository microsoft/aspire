// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Contains a process exit code and its complete output and error text.
/// </summary>
internal sealed record ProcessCaptureResult(int ExitCode, string StandardOutput, string StandardError);

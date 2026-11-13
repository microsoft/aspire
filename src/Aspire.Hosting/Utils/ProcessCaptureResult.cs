// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Contains a process exit code and its complete output and error text.
/// </summary>
/// <param name="ExitCode">The process exit code, including nonzero codes.</param>
/// <param name="StandardOutput">The complete captured standard output text.</param>
/// <param name="StandardError">The complete captured standard error text.</param>
internal sealed record ProcessCaptureResult(int ExitCode, string StandardOutput, string StandardError);

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory)]
    [ValidateRange(0, 255)]
    [int] $ExitCode
)

# Starting with .NET 11, `dotnet test` decides the zero-tests result for the whole run from
# aggregated results. `--ignore-exit-code 8` still makes direct test-module execution return 0,
# but an all-empty or all-skipped `dotnet test` run returns 8 from the orchestrator.
# https://learn.microsoft.com/dotnet/core/tools/dotnet-test-mtp#whole-run-and-per-module-minimums
if ($ExitCode -eq 8) {
    Write-Host "All selected tests were skipped; treating MTP exit code 8 as success."
    exit 0
}

exit $ExitCode

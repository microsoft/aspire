# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory)]
    [ValidateRange(0, 255)]
    [int] $ExitCode
)

if ($ExitCode -eq 8) {
    Write-Host "All selected tests were skipped; treating MTP exit code 8 as success."
    exit 0
}

exit $ExitCode

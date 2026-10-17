# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CommandPath,

    [Parameter(Mandatory)]
    [string[]] $ArgumentList,

    [string] $VersionOverrideArg = $env:VERSION_OVERRIDE_ARG
)

$versionOverrideArgs = @($VersionOverrideArg -split '\s+' | Where-Object { $_ })
& $CommandPath @ArgumentList @versionOverrideArgs

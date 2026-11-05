<#
.SYNOPSIS
Runs aspire add from this checkout in the caller's current directory.
.DESCRIPTION
Builds and runs the CLI from source using the configuration-source demo settings
and published packages. Additional arguments are forwarded to aspire add.
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1'
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1' redis
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1' Contoso.Hosting.LocalSource --version 1.0.0 --non-interactive
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $WorkingDirectory = (Get-Location).Path,

    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Arguments = @()
)

& (Join-Path $PSScriptRoot 'run-aspire-new.ps1') `
    -WorkingDirectory $WorkingDirectory -Command add -CommandArguments $Arguments

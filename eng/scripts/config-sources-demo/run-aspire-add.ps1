<#
.SYNOPSIS
Runs aspire add in the caller's current directory with the configuration-source demo settings.
.DESCRIPTION
Uses the installed CLI by default. Use -Cli source to build and run the CLI from
this checkout with published packages. Additional arguments are forwarded to aspire add.
Source builds display only errors.
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1'
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1' redis
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1' Contoso.Hosting.LocalSource --version 1.0.0 --non-interactive
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-add.ps1' -Cli source redis
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $WorkingDirectory = (Get-Location).Path,

    [ValidateSet('source', 'installed')]
    [string] $Cli = 'installed',

    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Arguments = @()
)

& (Join-Path $PSScriptRoot 'run-aspire-new.ps1') `
    -WorkingDirectory $WorkingDirectory -Cli $Cli -Command add -CommandArguments $Arguments

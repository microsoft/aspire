# Configuration-source demo

These PowerShell scripts run the installed Aspire CLI on `PATH` with the demo
configuration source by default. Both accept `-Cli installed` (the default) or
`-Cli source`. Source mode builds the CLI from this checkout, emulates the current
stable CLI version, and uses published packages, so a local package hive is not
required.

## Prerequisites

- For installed mode, have Aspire installed and available on `PATH`.
- For source mode on Windows, run `.\restore.cmd` from the repository root to set
  up the checkout's pinned .NET SDK.
- Have Git installed and authenticated with access to the configuration repository
  specified in [run-aspire-new.ps1](run-aspire-new.ps1).
- If a template or integration uses a private NuGet feed, authenticate to that feed
  separately using your normal NuGet credential setup. Git authentication does not
  grant access to package feeds.
- Install any SDKs or other prerequisites required by the selected template.

Source mode builds the CLI using the checkout's SDK, while child `dotnet`
commands honor the generated app's SDK pin. Installed mode does not build the CLI
or override its channel or version. Environment overrides and the caller's working
directory are restored when the scripts finish.

## Create an AppHost in a specified directory

Run the following from the repository root, changing `$appHostDirectory` to your
desired location:

```powershell
$demoScripts = Join-Path $PWD.Path 'eng\scripts\config-sources-demo'
$appHostDirectory = 'C:\Temp\ConfigSourcesDemo'

& (Join-Path $demoScripts 'run-aspire-new.ps1') `
    -WorkingDirectory $appHostDirectory `
    -CommandArguments @('--output', '.')
```

The script creates the working directory if necessary. `--output .` creates the
app in that directory rather than a nested directory. Choose the template,
language, and app name when prompted. Accept the configuration source when
prompted to apply the demo settings.

The CLI explains configuration sources before showing the proposed source details.
The launcher sets `ASPIRE_INIT_SOURCE_README_URL` to the configured branch's
[README](https://msft.ghe.com/coreai/aspire-1p-init-test/blob/jamesnk/source-demo/README.md),
so the details include a clickable link with more information. The previous
environment-variable value is restored when the launcher finishes. Terminals
without hyperlink support show the URL as plain text.

`-CommandArguments` forwards an array of arguments to `aspire new`. In the template picker, choose
**Search for more templates** and leave the search empty to list custom templates.

To use an automatically created temporary directory and the interactive prompts:

```powershell
& (Join-Path $demoScripts 'run-aspire-new.ps1')
```

The script prints the directory as `Demo workspace: ...`.

To build and run the CLI from this checkout instead:

```powershell
& (Join-Path $demoScripts 'run-aspire-new.ps1') -Cli source
```

## Add an integration to the created AppHost

Use the launcher below to apply the demo configuration-source settings to
`aspire add`. It uses the installed CLI by default, just like the new launcher.

Using the same variables from the creation example, open the integration picker:

```powershell
& (Join-Path $demoScripts 'run-aspire-add.ps1') `
    -WorkingDirectory $appHostDirectory
```

Or specify an integration and optional CLI arguments:

```powershell
& (Join-Path $demoScripts 'run-aspire-add.ps1') `
    -WorkingDirectory $appHostDirectory redis
```

Arguments after the script parameters are forwarded to `aspire add`, including
options such as `--version 1.0.0` and `--non-interactive`. To use the source-built CLI:

```powershell
& (Join-Path $demoScripts 'run-aspire-add.ps1') `
    -Cli source -WorkingDirectory $appHostDirectory redis
```

Without
`-WorkingDirectory`, the add script uses the caller's current directory:

```powershell
Push-Location -LiteralPath $appHostDirectory
try
{
    & (Join-Path $demoScripts 'run-aspire-add.ps1')
}
finally
{
    Pop-Location
}
```

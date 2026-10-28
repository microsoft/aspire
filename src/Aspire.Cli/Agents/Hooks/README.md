# Telemetry hooks

The Aspire CLI owns the two embedded `track-telemetry` scripts. `aspire agent
init` installs them under `~/.aspire/hooks` and writes supported client hook
registrations that execute the CLI's telemetry command.

Update the scripts through a normal Aspire pull request. Keep the Bash and
PowerShell implementations behaviorally aligned, and update their focused tests
when the recognized Aspire skills, MCP tools, or reference files change.

The release-driven
[Aspire skills update workflow](https://github.com/microsoft/aspire-skills/blob/dev/.github/workflows/update-aspire.md)
reviews released skill changes and opens draft pull requests against this
repository when telemetry needs to adapt. It must propose changes to these
Aspire-owned files rather than copy hook implementations from `aspire-skills`
or depend on the retired embedded-bundle maintenance scripts.

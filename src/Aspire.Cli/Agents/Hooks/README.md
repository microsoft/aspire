# Telemetry hooks

The Aspire CLI owns the two embedded `track-telemetry` scripts. `aspire agent
init` installs them under `~/.aspire/hooks` and writes supported client hook
registrations that execute the CLI's telemetry command.

Update the scripts through a normal Aspire pull request. Keep the Bash and
PowerShell implementations behaviorally aligned, and update their focused tests
when the recognized Aspire skills, MCP tools, or reference files change.

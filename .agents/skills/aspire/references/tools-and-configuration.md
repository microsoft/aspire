# Tools And Configuration

Use this when the task is about docs lookup, secrets, CLI configuration, diagnostics, cache cleanup, or local certificates.

## Scenario: I Need Docs Before I Change The AppHost Or Use An Unfamiliar API

Use these commands when the task is to confirm the right Aspire workflow before editing code.

```bash
aspire docs search <query>
aspire docs list
aspire docs get <slug>
```

Keep these points in mind:

- Use docs commands before changing integrations when you need to confirm the supported path.
- Use docs commands before implementing custom resource commands or unfamiliar AppHost APIs such as `WithCommand`.
- Use docs commands when the user needs help understanding an Aspire API, not just when they need a task workflow.
- Use `aspire docs list` when you need to browse the available doc set before narrowing to a specific page.

## Scenario: I Need To Inspect Or Change AppHost Secrets

Use these commands when the task is about AppHost user secrets such as connection strings, passwords, or API keys.

```bash
aspire secret set <key> <value>
aspire secret get <key>
aspire secret list
aspire secret path
aspire secret delete <key>
```

Keep these points in mind:

- Use `aspire secret` for AppHost user secrets instead of inventing another storage path.
- Use `aspire secret path` when the task is to locate the backing store without opening it manually.

## Scenario: I Need To Explain Where Aspire CLI Settings Came From

Use these commands when the question is about effective Aspire CLI configuration or conflicting local versus global settings.

```bash
aspire config set <key> <value>
aspire config get <key>
aspire config list
aspire config delete <key>
aspire config info
```

Keep these points in mind:

- Use `aspire config info` when the user wants to know where settings come from, which settings files are in play, or why the CLI is behaving a certain way locally.

## Scenario: My Local Aspire Setup Feels Broken

Use these commands when the local Aspire environment looks unhealthy and needs recovery steps rather than AppHost code changes.

```bash
aspire doctor
aspire cache clear
aspire certs trust
aspire certs clean
```

Keep these points in mind:

- Use `aspire doctor` early when the symptoms suggest local environment drift rather than an app bug.
- Use `aspire cache clear` when cached state is stale or interfering with normal operation.
- Use `aspire certs trust` and `aspire certs clean` when local certificate state is part of the problem.

# Tools And Configuration

Use this when the task is about docs lookup, API reference lookup, secrets, CLI configuration, diagnostics, cache cleanup, or local certificates.

## Scenario: I Need Docs Or API Reference Before I Change The AppHost

Use these commands when the task is to confirm the right Aspire workflow before editing code or retrieve a specific API reference entry.

```bash
aspire docs search <query>
aspire docs get <slug>
aspire docs api search <query> --language csharp|typescript
aspire docs api list <scope>
aspire docs api get <id>
```

Keep these points in mind:

- Use docs commands before changing integrations when you need to confirm the supported path or recommended workflow.
- Use docs commands before implementing custom resource commands or unfamiliar AppHost patterns such as `WithCommand`.
- Use `aspire docs api` when the user needs the C# or TypeScript reference entry for a specific Aspire API.
- Use `aspire docs api list <scope>` to browse children under a language, package, module, type, or symbol.

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
aspire config list --all
aspire config delete <key>
aspire config info
```

Keep these points in mind:

- Use `aspire config info` when the user wants to know where settings come from, which settings files are in play, or why the CLI is behaving a certain way locally.

## Scenario: I Need Default Watch Behavior

Use these commands when the user asks about Aspire default watch, wants to inspect whether it is enabled, or explicitly wants the CLI-managed default watch workflow.

```bash
aspire config list --all
aspire config set features.defaultWatchEnabled true
```

Keep these points in mind:

- `features.defaultWatchEnabled` controls Aspire default watch for AppHost execution.
- Default watch runs supported C# and TypeScript AppHosts in CLI watch mode and is restart-based for the AppHost-managed application.
- Do not treat default watch as per-resource rebuild, restart, or hot reload for resource source changes.
- Default watch is not a replacement for resource-specific runtime hot reload/watch, Aspire dashboard actions, Aspire CLI resource commands, or IDE-managed debugging.
- If VS Code, Visual Studio, or Rider is managing debugging or hot reload, let the IDE own that workflow instead of mixing it with Aspire CLI restart, rebuild, or watch behavior.

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

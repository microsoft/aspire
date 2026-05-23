# TypeScript AppHosts

Use this when the AppHost is `apphost.ts` and the task involves generated APIs or TypeScript-specific Aspire workflows.

## Scenario: I Added An Integration And Need New APIs To Show Up In `apphost.ts`

Use this when the task touches `.aspire/modules/` or newly added integrations.

```bash
aspire add <package>
```

Keep these points in mind:

- The `.aspire/modules/` folder contains generated TypeScript modules that expose Aspire APIs to the AppHost.
- Common generated files include `.aspire/modules/aspire.ts`, `base.ts`, and `transport.ts`.
- Do not edit `.aspire/modules/` directly.
- Use `aspire add <package>` to regenerate the available APIs after adding an integration.
- Inspect `.aspire/modules/aspire.ts` after `aspire add` to see the refreshed API surface available to `apphost.ts`.
- The local `tsconfig.json` often includes `.aspire/modules/**/*.ts` in its compilation scope.

## Scenario: `.aspire/modules/` Disappeared After A Pull, Clean, Or Branch Switch

Use this when generated support files are missing or stale and the TypeScript AppHost needs to be restored.

```bash
aspire restore
```

Keep these points in mind:

- Try `aspire restore` first when generated `.aspire/modules/*` files are missing.
- `aspire restore` restores and regenerates the TypeScript AppHost support files under `.aspire/modules/`.
- Do not manually recreate or edit generated module files.
- After recovery, inspect `.aspire/modules/aspire.ts` to confirm the available API surface.

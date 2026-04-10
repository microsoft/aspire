# JavaScript and TypeScript app patterns

Use this reference when wiring JavaScript/TypeScript services into the AppHost or configuring TypeScript AppHost dependencies (Step 5 and Step 6).

## Choosing the right JavaScript resource type

The `Aspire.Hosting.JavaScript` package provides three resource types. Pick the right one:

| Signal | Use | Example |
|--------|-----|---------|
| Vite app (has `vite.config.*`) | `AddViteApp(name, dir)` | Frontend SPA, Vite + React/Vue/Svelte |
| App runs via package.json script only | `AddJavaScriptApp(name, dir, { runScriptName })` | CRA app, Next.js, monorepo root scripts |
| App has a specific Node entry file (`.js`/`.ts`) and uses a dev script like `ts-node-dev` | `AddNodeApp(name, dir, "entry.js")` + `.WithRunScript("start:dev")` | Express/Fastify API, Socket.IO server |

**Key distinctions:**
- `AddNodeApp` is for apps that run a **specific file** with Node (e.g., an Express server at `src/index.ts`). Use `.WithRunScript("start:dev")` to override the dev-time command (e.g., `ts-node-dev`).
- `AddJavaScriptApp` runs a **package.json script** — simpler, good when the script handles everything.
- `AddViteApp` is `AddJavaScriptApp` with Vite-specific defaults (auto-HTTPS config augmentation, `dev` as default script).

## JavaScript dev scripts

Use `.WithRunScript()` to control which package.json script runs during development:

```typescript
// Express API with TypeScript: uses ts-node-dev for hot reload in dev
const api = await builder
    .addNodeApp("api", "./api", "src/index.ts")
    .withRunScript("start:dev")                      // runs "yarn start:dev" (ts-node-dev)
    .withYarn()
    .withHttpEndpoint({ env: "PORT" });

// Vite frontend: default "dev" script is fine, just add yarn
const web = await builder
    .addViteApp("web", "./frontend")
    .withYarn();
```

## Framework-specific port binding

Not all frameworks read ports from env vars the same way:

| Framework | Port mechanism | AppHost pattern |
|-----------|---------------|-----------------|
| Express/Fastify | `process.env.PORT` | `.withHttpEndpoint({ env: "PORT" })` |
| Vite | `--port` CLI arg or `server.port` in config | `.withHttpEndpoint({ env: "PORT" })` — Aspire's Vite integration handles this automatically |
| Next.js | `PORT` env or `--port` | `.withHttpEndpoint({ env: "PORT" })` |
| CRA | `PORT` env | `.withHttpEndpoint({ env: "PORT" })` |

When the framework supports reading the port from an env var or Aspire already handles it, **prefer that over pinning a fixed port**. Managed ports make repeated local runs more reliable and work better when multiple services or multiple Aspire apps are running.

**Suppress auto-browser-open:** Many dev servers (Vite, CRA, Next.js) auto-open a browser on start. Add `.withEnvironment("BROWSER", "none")` to prevent this in Aspire-managed apps. Vite also respects `server.open: false` in its config.

## TypeScript AppHost dependency configuration (Step 6)

### package.json

If one exists at the root, augment it (do not overwrite). Add/merge these scripts that delegate to the Aspire CLI:

```json
{
  "type": "module",
  "scripts": {
    "dev": "aspire run",
    "build": "tsc",
    "watch": "tsc --watch"
  }
}
```

If no root `package.json` exists, create a minimal one matching the canonical Aspire template:

```json
{
  "name": "<repo-name>",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "aspire run",
    "build": "tsc",
    "watch": "tsc --watch"
  },
  "engines": {
    "node": "^20.19.0 || ^22.13.0 || >=24"
  }
}
```

**Important**: Scripts should point to `aspire run`/`aspire start` — the Aspire CLI handles TypeScript compilation internally. Do not use `npx tsc && node apphost.js` patterns.

Never overwrite existing `scripts`, `dependencies`, or `devDependencies` — merge only. Do not manually add Aspire SDK packages — `aspire restore` handles those.

Run `aspire restore` to generate the `.modules/` directory with TypeScript SDK bindings, then install dependencies with the repo's package manager (`npm install`, `pnpm install`, or `yarn`).

### tsconfig.json

Augment if it exists:

- Ensure `".modules/**/*.ts"` and `"apphost.ts"` are in `include`
- Ensure `"module"` is `"nodenext"` or `"node16"` (ESM required)
- Ensure `"moduleResolution"` matches

If no `tsconfig.json` exists and `aspire restore` didn't create one, create a minimal one:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "nodenext",
    "moduleResolution": "nodenext",
    "esModuleInterop": true,
    "strict": true,
    "outDir": "./dist",
    "rootDir": "."
  },
  "include": ["apphost.ts", ".modules/**/*.ts"]
}
```

### ESLint

Only augment if config already exists. If it uses `parserOptions.project` or `parserOptions.projectService`, ensure the AppHost tsconfig is discoverable. Do not create ESLint configuration from scratch.

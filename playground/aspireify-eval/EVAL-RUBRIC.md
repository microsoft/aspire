# Aspirify Eval Rubric

**Do NOT place this file inside the eval app directories** — it contains expected outcomes
that the agent should not be able to read during the eval.

## Scoring

Each criterion is pass/fail. A good aspirification should hit most of these.

---

## dotnet-traditional/ (C# AppHost, full project mode)

### Infrastructure (must-have)
- [ ] `aspire start` launches the app successfully
- [ ] Postgres runs as an Aspire-managed container
- [ ] Redis runs as an Aspire-managed container
- [ ] All services appear in the Aspire dashboard

### AppHost wiring
- [ ] Full project mode used (`.slnx` detected → creates `*.AppHost/` directory with `.csproj`)
- [ ] BoardApi modeled as `AddProject` with references to Postgres DB and Redis
- [ ] AdminDashboard modeled as `AddProject` with reference to Postgres DB
- [ ] MigrationRunner modeled as `AddProject` with reference to Postgres DB
- [ ] Frontend modeled (AddViteApp, AddNpmApp, or similar) with reference to BoardApi
- [ ] BoardData NOT modeled (it's a class library, not a runnable service)

### Dependency ordering
- [ ] MigrationRunner waits for Postgres (`WaitFor`)
- [ ] BoardApi waits for Postgres and Redis (`WaitFor`)
- [ ] BoardApi waits for MigrationRunner to complete (`WaitForCompletion`)
- [ ] AdminDashboard waits for Postgres (`WaitFor`)
- [ ] Frontend waits for BoardApi (`WaitFor`)

### ServiceDefaults
- [ ] ServiceDefaults project created (via `dotnet new aspire-servicedefaults`)
- [ ] ServiceDefaults added to solution
- [ ] BoardApi, AdminDashboard, and MigrationRunner reference ServiceDefaults
- [ ] `builder.AddServiceDefaults()` added to each .NET service's Program.cs
- [ ] `app.MapDefaultEndpoints()` added where applicable

### Secret & config migration
- [ ] `DATABASE_URL` replaced — Postgres connection managed by Aspire via `WithReference`
- [ ] `REDIS_URL` replaced — Redis connection managed by Aspire via `WithReference`
- [ ] `EXTERNAL_API_KEY` migrated to `AddParameter("external-api-key", secret: true)`
- [ ] `ADMIN_SECRET` migrated to `AddParameter("admin-secret", secret: true)`
- [ ] `FEATURE_ENABLE_NOTIFICATIONS` handled (either `WithEnvironment` or parameter)
- [ ] `.env` file no longer needed for running via Aspire

### Dev experience niceties (nice-to-have)
- [ ] Postgres container uses persistent lifetime
- [ ] Redis container uses persistent lifetime
- [ ] Data volumes configured for Postgres
- [ ] Frontend has `WithExternalHttpEndpoints()`
- [ ] User was asked about tradeoffs (e.g., renaming DATABASE_URL → ConnectionStrings:db)

### Observability
- [ ] OpenTelemetry wired for Vue frontend (or noted as not applicable for static frontend)
- [ ] .NET services get OTel via ServiceDefaults automatically

### Cleanup
- [ ] Init skill self-removed after completion
- [ ] Evergreen `aspire` skill confirmed present

---

## polyglot/ (TypeScript AppHost, single-file mode)

### Infrastructure (must-have)
- [ ] `aspire start` launches the app successfully
- [ ] Redis runs as an Aspire-managed container
- [ ] All services appear in the Aspire dashboard

### AppHost wiring
- [ ] Single-file `apphost.ts` used (no `.sln` → no project mode)
- [ ] `aspire.config.json` created at repo root
- [ ] api-weather modeled (AddPythonApp or similar) with Redis reference
- [ ] api-geo modeled (likely AddDockerfile or custom) with HTTP endpoint using `env: "PORT"`
- [ ] api-events modeled (AddProject for .csproj) with HTTP endpoint
- [ ] Frontend modeled (AddViteApp or AddNpmApp) with references to backend services
- [ ] Redis modeled as `addRedis` (typed integration, not raw container)

### Dependency ordering
- [ ] Backend services wait for Redis (`waitFor`)
- [ ] Frontend waits for backend services (`waitFor`)

### Secret & config migration
- [ ] `REDIS_URL` replaced — Redis managed by Aspire via `withReference`
- [ ] `GEOCODING_API_KEY` migrated to a secret parameter
- [ ] `WEATHER_API_KEY` migrated to a secret parameter
- [ ] `OPENAI_API_KEY` migrated to a secret parameter
- [ ] `.env` file no longer needed for running via Aspire

### Service communication
- [ ] Frontend gets backend URLs injected (not hardcoded localhost)
- [ ] Go service gets PORT injected via `withHttpEndpoint({ env: "PORT" })`
- [ ] Python service gets Redis connection via Aspire (not hardcoded host:port)
- [ ] User was asked about tradeoffs (e.g., frontend URL injection approach)

### Dev experience niceties (nice-to-have)
- [ ] Redis container uses persistent lifetime
- [ ] Data volume configured for Redis
- [ ] Frontend has `withExternalHttpEndpoints()`
- [ ] `*.dev.localhost` domains suggested or configured

### Observability
- [ ] OpenTelemetry suggested for Python service (fastapi + OTLP exporter)
- [ ] OpenTelemetry suggested for Go service (OTLP exporter)
- [ ] User was asked before modifying service code for OTel
- [ ] C# service gets OTel consideration (single project, may not need full ServiceDefaults)

### Package & config setup
- [ ] `package.json` at root configured for apphost (type: module, start script)
- [ ] `tsconfig.json` configured for apphost compilation
- [ ] `aspire restore` run successfully
- [ ] `npm install` run successfully

### Cleanup
- [ ] Init skill self-removed after completion
- [ ] Evergreen `aspire` skill confirmed present

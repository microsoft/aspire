# Aspire Dashboard → Deck redesign — validation run recipe

This file is for contributors working on the Dashboard → Deck visual redesign (Blazor dashboard
restyled to match the Aspire Deck preview UI). It documents how to run the dashboard locally with
sample data and drive it with Playwright — without going through login/token flows.

> Shared-environment note: the dev box is shared by multiple parallel sessions. **Pick unique
> ports per session** (e.g. derive every port from a per-session base offset). Two sessions on the
> same port will cross-contaminate (your dashboard can end up showing another session's data).

## Build first

From the repo root (once per machine): `./restore.sh`, then build the dashboard. Use the local SDK:

```bash
export PATH="$PWD/.dotnet:$PATH"
MSBUILDTERMINALLOGGER=false dotnet build src/Aspire.Dashboard/Aspire.Dashboard.csproj -p:SkipNativeBuild=true
```

The default build treats analyzer warnings as errors (IDE0005 unused usings, CA1822, etc.), so keep
it clean. To iterate while temporarily ignoring warnings, add `/p:TreatWarningsAsErrors=false`.

## Option A — standalone unsecured dashboard + demo harness (Playwright-friendly, recommended)

The standalone dashboard with `Frontend:AuthMode=Unsecured` needs **no login token**, so Playwright
can hit the URL directly. Point its resource-service client at the demo's gRPC port; point the
demo's OTLP exporter at the dashboard's OTLP/HTTP endpoint.

Choose a unique base port (here `BASE=16700` as an example) and derive the rest:

```bash
# --- ports (make BASE unique per session) ---
FRONTEND=16771        # dashboard frontend (Playwright hits http://localhost:$FRONTEND)
RS_PORT=19771         # demo gRPC resource service
OTLP_GRPC=4391        # dashboard OTLP/gRPC listener
OTLP_HTTP=4392        # dashboard OTLP/HTTP listener (demo pushes telemetry here)
TRIGGER=58771         # demo control endpoint

# --- 1. demo resource service + telemetry (from /tmp/aspire-deck-demo) ---
cd /tmp/aspire-deck-demo
DEMO_RS_PORT=$RS_PORT DEMO_OTLP_HTTP_URL=http://localhost:$OTLP_HTTP \
  DEMO_APP_NAME=deck-demo-shop DEMO_TRIGGER_PORT=$TRIGGER \
  npx tsx demo.ts            # serves ~8 resources incl. 3 Parameters; prints the gRPC port

# --- 2. dashboard, unsecured frontend, pointed at the demo ---
cd <repo-root>
export PATH="$PWD/.dotnet:$PATH"
ASPNETCORE_ENVIRONMENT=Development \
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
Dashboard__Frontend__AuthMode=Unsecured \
Dashboard__Otlp__AuthMode=Unsecured \
Dashboard__ResourceServiceClient__AuthMode=Unsecured \
ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=http://localhost:$RS_PORT \
ASPNETCORE_URLS=http://localhost:$FRONTEND \
ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:$OTLP_GRPC \
ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:$OTLP_HTTP \
  dotnet run --no-build --project src/Aspire.Dashboard/Aspire.Dashboard.csproj --no-launch-profile
```

Then drive it: `playwright-cli open http://localhost:$FRONTEND/`.

### Auth-ENABLED variant (for the Login page session)

`AuthMode=Unsecured` skips login entirely, so to render/validate the **login page** use
`BrowserToken` instead. Swap the frontend auth env vars in step 2 above:

```bash
# replace  Dashboard__Frontend__AuthMode=Unsecured  with:
Dashboard__Frontend__AuthMode=BrowserToken \
Dashboard__Frontend__BrowserToken=devtoken12345 \
```

- Navigate to `http://localhost:$FRONTEND/` → it redirects to `/login`, which renders the Deck/login
  page (this is what the Login session restyles).
- To actually authenticate and reach the app, hit `http://localhost:$FRONTEND/login?t=devtoken12345`
  (the token query param is `t`), or paste the token into the login form.
- The resource-service/OTLP env vars are unchanged; only the frontend auth mode differs.

### Demo control endpoint (interactions)

The demo opens a `WatchInteractions` stream and can inject interactions on demand:

```bash
# Inject a command-input interaction (the apiservice "Scale…" command) -> Deck InteractionPane
curl -X POST http://127.0.0.1:$TRIGGER/trigger-scale
```

The demo's `apiservice` resource also exposes the `Scale…` command in its details drawer, which
raises the same inputs dialog (number/choice/boolean fields with live validation).

### Gotchas

- **Resource property values must use camelCase** in the demo proto JSON (e.g. `stringValue`,
  `string_value` works for some fields but not others) — mismatched casing is silently dropped
  (that's why the demo's SOURCE column shows "—").
- **Nesting:** the base demo has no parent/child resources. To exercise the nested-resource chevron,
  add a resource with a `resource.parentName` property (value `{ stringValue: "<parentName>" }`)
  whose value matches a parent resource's `name`.
- The dashboard binds **both** OTLP gRPC and HTTP listeners; set both env vars to unique ports to
  avoid `EADDRINUSE`.

## Option B — Stress playground (realistic end-to-end AppHost, ~34 resources)

```bash
export PATH="$PWD/.dotnet:$PATH"
dotnet run --project playground/Stress/Stress.AppHost --launch-profile http
```

The AppHost launches its own dashboard and prints the dashboard URL (and a login token) in its
output. Good for a realistic check including nested resources. Caveat: the current `aspire run`-style
launcher shows a TUI ("Connecting to AppHost…") and the dashboard login URL isn't trivially parseable
for fully-headless Playwright automation — so for scripted Playwright runs, Option A is more reliable;
use Stress when you can interact with the launched dashboard window.

## What to validate per surface

- Renders in **both light and dark** (the dashboard sets `data-theme` on `<html>`, which drives the
  Deck token sets in `wwwroot/css/deck-theme.css`).
- Matches the corresponding Deck page under `src/Aspire.Deck/ui/src/` (layout, spacing, states).
- No console exceptions; existing dashboard functionality still works.

## CSS convention for parallel work

`wwwroot/css/deck.css` is Deck's `global.css` copied + adapted. To keep parallel page work
merge-clean, **append** a clearly-delimited block at the end of the file per surface:

```css
/* === <PageName> === */
...your page-scoped rules...
```

Do not restructure the shared Deck-derived rules above. Keep page component edits within your own
page's files.

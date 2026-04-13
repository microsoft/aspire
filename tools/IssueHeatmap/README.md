# Issue Heatmap Tool

Generates a visual heatmap of all open issues in the `dotnet/aspire` repository, categorized by feature quadrant.

## Quadrants

| Quadrant | What it covers |
|---|---|
| **Integrations** | Technology integrations — Redis, PostgreSQL, SQL Server, MongoDB, Kafka, Azure services (CosmosDB, Storage, ServiceBus, etc.), EF Core, YARP, Orleans, container runtimes |
| **Inner Loop** | Developer experience — CLI, tooling, IDE integration, debugging, hot reload, dev tunnels, AI-assisted dev, MCP, acquisition/installation |
| **Polyglot** | App model & type system — resource lifecycle, endpoints, volumes, parameters, secrets, service discovery, health checks, templates, telemetry/OTel, multi-language support (Python/Node.js/TypeScript app hosts), exports |
| **Deployment** | Publishing & cloud — Azure Container Apps, Kubernetes, Docker Compose, Azure Functions, Bicep, Terraform, Azure provisioning |
| **Dashboard** | Dashboard UI — resource views, logs/traces/metrics viewers, filtering, accessibility, settings |
| **Infra** | Engineering systems — CI/CD, test infrastructure, flaky/failing tests, build tooling, samples |

## Prerequisites

- Python 3.10+
- `gh` CLI (authenticated with access to `dotnet/aspire`)
- Python packages: `pip install matplotlib seaborn numpy`

## Usage

```bash
# From the repo root:
python tools/IssueHeatmap/generate_heatmap.py

# Or with options:
python tools/IssueHeatmap/generate_heatmap.py --repo dotnet/aspire --output tools/IssueHeatmap/output
```

This will:
1. Fetch all open issues via `gh issue list`
2. Classify each issue into a quadrant and bug/feature type using label + title + body heuristics
3. Generate a heatmap PNG and a CSV with all classifications

Output files are saved to `tools/IssueHeatmap/output/` with the current date.

## AI-Enhanced Classification

For higher accuracy classification using generative AI (recommended for periodic reports), use the Copilot CLI:

```
Ask Copilot: "Generate an issue heatmap for aspire using AI classification"
```

This will classify each issue individually using an LLM rather than heuristics, producing more accurate results especially for issues with ambiguous labels.

## Output

- `aspire_heatmap_YYYY-MM-DD.png` — 6-panel visualization with count heatmap, stacked bars, donut chart, age heatmap, bug ratio, and key themes
- `aspire_issues_YYYY-MM-DD.csv` — Full classification data (number, url, title, quadrant, type, age_days, created, labels)

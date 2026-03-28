# Aspire Roadmap - Q1 2026 Update (March 2026)

Since our [December 2025 update](https://github.com/dotnet/aspire/discussions/12810), we've shipped **Aspire 13.2** (March 2026). This post summarizes our progress against the [original roadmap](https://github.com/dotnet/aspire/discussions/10644) and highlights what's next.

## 🎉 Major Accomplishments

**Aspire 13.0** marked a transformational shift from a .NET-exclusive tool to a **full polyglot application platform**, elevating Python and JavaScript to first-class status alongside .NET.

**Aspire 13.1** introduced comprehensive **AI coding agent support** through MCP integration and stabilized several key features.

**Aspire 13.2** focused on **tooling polish and infrastructure hardening** — shipping the long-awaited `aspire doctor` diagnostics command, `aspire certs` certificate management, Homebrew and WinGet packaging for frictionless installation, and the aspire-managed unified binary. Azure AI Foundry Agent support also shipped this cycle.

**Rebranded to Aspire** - We've dropped the ".NET" prefix to reflect our polyglot future. Same great tool, broader vision.

**[aspire.dev](https://aspire.dev)** is our official documentation site.

**[YouTube Channel](https://www.youtube.com/@aspiredotdev)** for technical demos and content.

---

## 🧑‍💻 Local Developer Experience

| Feature | Status |
|---------|--------|
| `.localhost` DNS Support | ✅ Shipped (13.0) |
| Local HTTPS Everywhere | ✅ Shipped (13.0/13.1 - TLS termination APIs) |
| Dev Tunnels | ✅ Shipped & Stabilized (13.1) |
| Run Subsets | 🔄 In progress |
| Container Commands | 🔄 In progress (rebuild command shipped in 13.2) |
| Debug .NET in Containers | 🔲 Not started |
| Multi-Repo Support | 🔲 Not started |
| Runtime Acquisition | ✅ Shipped (13.2 - aspire-managed unified binary, SDK auto-install) |

---

## 🧪 Testing Experience

| Feature | Status |
|---------|--------|
| Live Dashboard During Tests | 🔲 Not started |
| Capture & Replay | 🔲 Not started |
| Partial AppHost Execution | 🔲 Not started |
| Request Redirection & Mocking | 🔲 Not started |
| Code Coverage Support | 🔲 Not started |

---

## 📊 Dashboard Enhancements

| Feature | Status |
|---------|--------|
| Storage Support | 🔲 Not started |
| Custom Layouts & Grouping | 🔲 Not started |
| Cross-Linked Navigation | ✅ Shipped (9.5) |
| Deploy Anywhere | 🔲 Not started |
| Enhanced Search & Filtering | ✅ Shipped (13.1) |
| Path Base & Reverse Proxy Support | ✅ Shipped (9.5) |
| **AI/GenAI Enhancements** | ✅ Shipped (13.0/13.1 - MCP server, GenAI visualizer with tools, evaluations, token usage) |
| **Parameters Tab** | ✅ Shipped (13.1) |

---

## 🛠 Tooling

| Feature | Status |
|---------|--------|
| Aspire CLI (`init`, `update`, `run`) | ✅ Shipped (13.0) |
| Aspire CLI (`new`, `do`) | ✅ Shipped (13.0) |
| Package Managers (Homebrew/Winget) | ✅ Shipped (13.2) |
| Single-File AppHost | ✅ Shipped (13.0) |
| VS Code Extension | ✅ Shipped & Stabilized (13.1 - v1.0.0) |
| **MCP Integration (`aspire mcp init`)** | ✅ Shipped (13.1) |
| **`aspire doctor` (environment diagnostics)** | ✅ Shipped (13.2) |
| **`aspire certs` (certificate management)** | ✅ Shipped (13.2) |

---

## 🌐 Polyglot Support

| Feature | Status |
|---------|--------|
| Consistent Client Integrations | 🔄 In progress |
| Templates & Samples | ✅ Shipped (13.0/13.1 - Python, JavaScript, TypeScript starters) |
| Cross-Language AppHost (WASM + WIT) | 🔲 Not started |
| **Python First-Class Support** | ✅ Shipped (13.0 - uv, pip, Uvicorn, Dockerfile generation) |
| **JavaScript First-Class Support** | ✅ Shipped (13.0 - npm, yarn, pnpm, Vite support) |

---

## 🤖 AI Workflows

| Feature | Status |
|---------|--------|
| Azure AI Foundry Agent Support | ✅ Shipped (13.2 - Foundry Agents Hosting Integration) |
| OpenAI Integration | ✅ Shipped (13.0) |
| Aspire MCP Server | ✅ Shipped (13.0/13.1 - full MCP integration with coding agents) |
| **AI Coding Agent Support** | 🔄 In progress (13.1/13.2 - VS Code, GitHub Copilot, Claude Code) |

---

## ☁ Hosting Integrations

| Feature | Status |
|---------|--------|
| Entra ID / Keycloak / Easy Auth | 🔄 In progress |
| VNet Modeling | 🔄 In progress |
| Event Grid | 🔲 Not started |
| Front Door | 🔲 Not started |
| APIM | 🔲 Not started |
| Kusto | ✅ Shipped (13.1 - Kusto Explorer commands) |
| **Azure Managed Redis** | ✅ Shipped (13.1 - renamed from Redis Enterprise) |
| **.NET MAUI** | ✅ Shipped (13.0) |

---

## 🚀 Deployment

| Target | Status |
|--------|--------|
| Azure Container Apps (ACA) | ✅ Most Complete |
| Docker Compose | ✅ Shipped & Stabilized (13.2) |
| Kubernetes | 🔄 In progress |
| Azure App Service | ✅ Shipped (13.1 - deployment slots, auto-scaling) |
| Azure Functions on ACA | ✅ Shipped & Stabilized (13.1) |
| ACA Jobs | ✅ Shipped |
| AKS | 🔄 In progress (13.2 - E2E tests, publisher fixes) |
| Polyglot Azure Functions | 🔄 In progress (13.2 - polyglot exports added) |

**Pipeline & CI/CD:**
- ✅ `aspire do` pipeline system shipped (13.0)
- 🔄 GitHub Actions / Azure DevOps / GitLab generation in progress

---

## 📈 By the Numbers

| Release | Date | Key Theme |
|---------|------|-----------|
| 9.5 | September 2025 | Dev Tunnels, Dashboard cross-linking |
| 13.0 | November 2025 | Polyglot platform (Python, JavaScript first-class) |
| 13.1 | December 2025 | AI coding agents, feature stabilization |
| 13.2 | March 2026 | Tooling polish & infrastructure hardening |

---

## 🔮 Looking Ahead

**Testing** remains our largest gap. We're actively experimenting with new testing experiences to make tests behave consistently across local development, CI, and different operating systems. More details coming soon.

**Running a subset of Aspire resources** is critical for larger projects. We're making good progress with prototypes and targeting a Q2 ship. **Multi-repo support** continues to be a popular community ask, especially for enterprise teams with distributed codebases. Similarly, **dashboard persistence** (external storage for telemetry) remains on our radar.

**Debugging in containers** is one of our most popular asks and we're committed to tackling it in 2026. This requires engineering work in both Visual Studio and VS Code to make the experience as seamless as we want it to be.

We're continuing to expand **`aspire do`** to cover more end-to-end scenarios across the full development lifecycle, from environment setup and dependency installation through testing, building, deployment, and environment promotion. GitHub Actions and Azure DevOps pipeline generation are the next priorities here.

Finally, **AI workflows** continue to evolve rapidly. With Azure AI Foundry Agent support now shipped, we're focused on deepening AI coding agent integration and expanding what the MCP server can do for your development workflow.

---

## 📚 Resources

- **Official docs**: [aspire.dev](https://aspire.dev)
- **What's new in 13.0**: [aspire.dev/whats-new/aspire-13](https://aspire.dev/whats-new/aspire-13/)
- **What's new in 13.1**: [aspire.dev/whats-new/aspire-13-1](https://aspire.dev/whats-new/aspire-13-1/)
- **What's new in 13.2**: [aspire.dev/whats-new/aspire-13-2](https://aspire.dev/whats-new/aspire-13-2/)

---

> **Disclaimer**: This roadmap is directional, not a commitment. Plans may evolve as priorities shift, feedback is incorporated, or technical constraints emerge.

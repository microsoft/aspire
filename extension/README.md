# Aspire for Visual Studio Code

The official Aspire extension for VS Code. Run, debug, and deploy your Aspire apps without leaving the editor, with debugging support for C#, Python, Node.js, Go, and more.

Aspire helps you build distributed apps — microservices, databases, containers, frontends, and any complex application topology — by providing declarative APIs to wire your resources together in code.

---

## Getting Started

Open your Aspire project in VS Code, or create one with **Aspire: New Aspire project** from the Command Palette. Run **Aspire: Configure launch.json file** to set up the debug configuration, then press **F5**. The Aspire extension will build your apphost, start your services, attach debuggers, and print the dashboard URL. Open the dashboard from the Aspire panel when you need it, or opt into auto-launch with the `aspire.dashboardBrowser` setting.

There's also a built-in walkthrough at **Help → Get Started → Get started with Aspire** that covers the basics step by step.

The core flow is:

1. Open an Aspire repo or create a starter with **Aspire: New Aspire project**.
2. Let the Aspire view discover the AppHost.
3. Press **F5**, or use **Run Aspire apphost** / **Debug Aspire apphost** from the editor.
4. Inspect resources in the Aspire view and open the dashboard when you need logs, traces, metrics, or endpoint URLs.

Learn more in the [Aspire VS Code extension documentation](https://aspire.dev/get-started/aspire-vscode-extension/).

---

## Running and Debugging

### Launch configuration

Add an entry to `.vscode/launch.json` pointing at your apphost:

```json
{
    "type": "aspire",
    "request": "launch",
    "name": "Aspire: Launch TypeScript starter",
    "program": "${workspaceFolder}/AppHost/apphost.mts"
}
```

When you hit **F5**, the extension builds the apphost, starts all the resources (services, containers, databases) in the right order, hooks up debuggers based on each service's language, and prints the dashboard URL.

You can also right-click an AppHost file in the Explorer and pick **Run Aspire apphost** or **Debug Aspire apphost**.

![VS Code running and debugging an Aspire AppHost with resource debug sessions.](https://raw.githubusercontent.com/microsoft/aspire/main/extension/resources/vscode-extension-debug-session.png)

### Deploy, publish, and pipeline steps

The `command` property in the launch config lets you do more than just run:

- **`deploy`** — push to your defined deployment targets.
- **`publish`** — generate deployment artifacts (manifests, Bicep files, etc.).
- **`do`** — run a specific pipeline step. Set `step` to the step name.

```json
{
    "type": "aspire",
    "request": "launch",
    "name": "Aspire: Deploy TypeScript starter",
    "program": "${workspaceFolder}/AppHost/apphost.mts",
    "command": "deploy"
}
```

### Customizing debugger settings per language

The `debuggers` property lets you pass debug config specific to a language. Use `project` for C#/.NET services, `python` for Python, and `apphost` for the apphost itself:

```json
{
    "type": "aspire",
    "request": "launch",
    "name": "Aspire: Launch MyAppHost",
    "program": "${workspaceFolder}/MyAppHost/MyAppHost.csproj",
    "debuggers": {
        "project": {
            "console": "integratedTerminal",
            "logging": { "moduleLoad": false }
        },
        "apphost": {
            "stopAtEntry": true
        }
    }
}
```

---

## The Aspire panel

The extension adds an **Aspire** panel to the Activity Bar. It shows a live tree of your resources. In **Workspace** mode you see resources from the apphost in your current workspace, updating in real time. Switch to **Global** mode with the toggle in the panel header to see every running apphost on your machine.

Right-click a resource to start, stop, or restart it, view its logs, run resource-specific commands, or open the dashboard.

![Aspire view discovering a workspace AppHost and showing resources with live state.](https://raw.githubusercontent.com/microsoft/aspire/main/extension/resources/vscode-extension-apphost-view.png)

---

## The Aspire Dashboard

The dashboard gives you a live view of your running app — all your resources and their health, endpoint URLs, console logs from every service, structured logs (via OpenTelemetry), distributed traces across services, and metrics. By default, the dashboard URL is printed when your app starts and stays available from the Aspire panel.

![Aspire Dashboard showing running resources](https://raw.githubusercontent.com/microsoft/aspire/main/extension/resources/aspire-dashboard-dark.png)

---

## Language and Debugger Support

The extension figures out what language each resource uses and attaches the right debugger. Some languages need a companion extension:

| Language | Debugger | Extension needed |
|----------|----------|------------------|
| C# / .NET | coreclr | [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) or [C#](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp) |
| Python | debugpy | [Python](https://marketplace.visualstudio.com/items?itemName=ms-python.python) |
| Node.js | js-debug (built-in) | None |
| Browser apps | js-debug (built-in) | None |
| Azure Functions | varies by language | [Azure Functions](https://marketplace.visualstudio.com/items?itemName=ms-azuretools.vscode-azurefunctions) + language extension |

---

## Feedback and Issues

Found a bug or have an idea? File it on the [microsoft/aspire](https://github.com/microsoft/aspire/issues) repo:

- [Report a bug](https://github.com/microsoft/aspire/issues/new?template=10_bug_report.yml&labels=area-vscode-extension)
- [Request a feature](https://github.com/microsoft/aspire/issues/new?template=20_feature-request.yml&labels=area-vscode-extension)

### Contributing

See [CONTRIBUTING.md](https://github.com/microsoft/aspire/blob/main/extension/CONTRIBUTING.md) for setup, project layout, the extension-only inner loop, and running tests. Good first issues are tagged [`area-vscode-extension` + `good first issue`](https://github.com/microsoft/aspire/issues?q=is%3Aissue+is%3Aopen+label%3Aarea-vscode-extension+label%3A%22good+first+issue%22).

### Learn more

- [Aspire docs](https://aspire.dev/docs/)
- [Integration gallery](https://aspire.dev/integrations/gallery/)
- [Dashboard overview](https://aspire.dev/dashboard/overview/)
- [Discord](https://discord.com/invite/raNPcaaSj8)

---

## License

See [LICENSE.TXT](./LICENSE.TXT) for details.

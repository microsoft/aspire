# Run your app

Use the **Run apphost** or **Debug apphost** buttons in the sidebar, or run the commands from the Command Palette. You can also right-click an apphost in the Aspire view.

When you run, the extension:
1. Discovers the apphost project in your workspace
2. Builds your solution
3. Launches all services in the correct order
4. Opens the **Aspire Dashboard** for real-time monitoring

### Debugging
When you **debug** instead of run, the extension attaches debuggers to your services automatically — set breakpoints in any project and they'll be hit as requests flow through your app.

### Editor indicators
While your app is running, the extension shows live resource status directly in your apphost source file:

- **Gutter icons** — distinct shapes in the editor gutter next to each resource definition show state at a glance: a green **✓** checkmark for running healthy, a yellow **⚠** triangle for unhealthy or degraded, a red **✕** for errors, a blue **⌛** hourglass for starting or waiting, and a grey **○** circle for not yet started.
- **Code lens** — inline labels above each resource show the current state and health (e.g. "Running - (Unhealthy 0/1)", "Not Started", "Waiting") along with quick actions like Restart, Stop, Start, and Logs.

![Editor showing gutter icons and code lens labels for resources in different states](../resources/editor-indicators-dark.png)

### The dashboard
Once running, the dashboard shows all your resources, endpoints, logs, traces, and metrics in one place:

![Aspire dashboard showing running resources](../resources/aspire-dashboard-dark.png)

To learn more, see the [Aspire Dashboard overview](https://aspire.dev/dashboard/overview/).

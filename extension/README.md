# Your whole distributed app, one F5 away.

![Aspire logo](https://raw.githubusercontent.com/microsoft/aspire/main/extension/aspire-logo-128.png)

The official Aspire extension for Visual Studio Code brings your AppHost, resources, debuggers, and dashboard into one development loop.

[Get started](https://aspire.dev/get-started/aspire-vscode-extension/) ·
[Documentation](https://aspire.dev/docs/) ·
[Integrations](https://aspire.dev/integrations/gallery/)

## Start here

An **AppHost** defines your app in code: services, containers, databases, front ends, and the connections between them.

1. Open an Aspire project or create a new one using the Aspire view.
2. Choose your AppHost and press **F5**. Aspire starts every resource in the right order and connects supported debuggers.
3. Use the **Aspire** view and **Aspire Dashboard** to follow resources, endpoints, logs, traces, and metrics.

## A better inner dev loop

- **Run your apps:** Start, debug, and stop an Aspire application, or use the available start, stop, and restart actions for individual resources, from the Aspire view.
- **Debug across your stack:** Aspire debug sessions support C#, JavaScript, Python, Go, Java, and Rust application resources when the corresponding debugger is available. Java and Rust debugging require Aspire 13.6 or later.
- **Attach to running resources:** Attach to .NET and Go resources from the Aspire view. If a debugger extension is missing or disabled, open Extensions from the notification, install or enable it, and retry without restarting the AppHost.
- **See live health and analytics:** Open the Aspire dashboard for resource health, endpoints, console logs, structured logs, distributed traces, and metrics.
- **Monitor without leaving VS Code:** See resource health summaries and quick actions beside resource definitions in your AppHost.
- **Move toward production.** Deploy, publish artifacts, and run pipeline steps using the Aspire view.

## Bring your stack

Aspire can bring together C#, TypeScript and JavaScript, Python, Go, Java, containers, databases, cloud resources, and more. Browse the [integration gallery](https://aspire.dev/integrations/gallery/) to find the pieces your app needs.

## Chat tools for agents

The extension contributes `aspire_apphost_start`, `aspire_apphost_stop`, and `aspire_resource_debug` language model tools. They only accept AppHosts and resources already discovered in a trusted workspace, reject arbitrary or absolute paths, and ask the user to confirm before acting. Resource debugging attaches to a running resource, including .NET and Go resources started with **Run Without Debugging** in VS Code; it does not start or restart one.

Go resources started with **Run Without Debugging** use DCP-managed `go run`, just like CLI starts, so the debugger can attach later. Actual debug launches still use VS Code and Delve; `debuggers.go` overrides apply to those IDE-managed launches.

Attachment to a .NET resource launched by `dotnet watch` is unavailable when arguments follow its first `--`. SDK versions interpret that separator differently, so Aspire declines attachment rather than guessing which build output is running. The resource continues running; normal `dotnet run` launches are unaffected.

Resource-debug confirmation is bound to the resolved AppHost. If discovery initially fails or a path points to a different AppHost before attachment, the tool refuses the operation; retry with a fresh confirmation after the intended AppHost has been discovered.

## Learn more

- [Aspire extension for Visual Studio Code](https://aspire.dev/get-started/aspire-vscode-extension/)
- [Build your first Aspire app](https://aspire.dev/get-started/first-app/)
- [Explore the Aspire Dashboard](https://aspire.dev/dashboard/overview/)
- [Read the Aspire documentation](https://aspire.dev/docs/)

## Feedback and contributions

Found a bug or have an idea? [Report an issue](https://github.com/microsoft/aspire/issues/new?template=10_bug_report.yml&labels=area-vscode-extension) or [request a feature](https://github.com/microsoft/aspire/issues/new?template=20_feature-request.yml&labels=area-vscode-extension).

Interested in contributing to the Aspire VS Code experience? Read the [contributor guide](https://github.com/microsoft/aspire/blob/main/extension/CONTRIBUTING.md) and pick up a [`good first issue`](https://github.com/microsoft/aspire/issues?q=is%3Aissue+is%3Aopen+label%3Aarea-vscode-extension+label%3A%22good+first+issue%22).

## License

See [LICENSE.TXT](./LICENSE.TXT) for details.

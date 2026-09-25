# Go Debugging Playground

This playground is for manually testing Aspire's VS Code Go debugger integration.

1. Open the Aspire repo in VS Code.
2. Start the `Run Extension (Go debugging playground)` launch configuration.
3. In the Extension Development Host, install the recommended Go extension if prompted.
4. Start the `Debug Go Debugging AppHost` launch configuration.
5. Set a breakpoint in `api/main.go`.

The AppHost references the local `Aspire.Hosting.Go` project so it exercises the current repo changes. The Go API uses the `playground` build tag and receives `--message hello-from-aspire` as app args, which verifies that the Aspire launch configuration passes `buildFlags` to VS Code and strips Go tool arguments from the debugged program args.

# Aspire.Hosting.CodeGeneration.Go

Provides Go AppHost scaffolding and generates Go bindings for Aspire hosting APIs.
This is tooling for writing the AppHost in Go, not an integration for hosting a
Go application.

## Getting started

Go AppHost support is experimental. Install the Aspire CLI and Go 1.26 or later.
In a new directory for your AppHost, enable the feature and initialize it:

```bash
aspire config set features.experimentalPolyglot:go true
aspire init --language go
```

The CLI restores this code-generation package automatically for a Go AppHost.
Do not install it with `aspire add`.

Edit `apphost.go` to define your resources. The scaffolded `go.mod` maps
`apphost/modules/aspire` to the generated SDK in `.aspire/modules/`.

## Generated bindings

Bindings are generated from Aspire Type System (ATS) metadata in `Aspire.Hosting`
and the hosting integrations configured in `aspire.config.json`. The generated
Go API calls the .NET AppHost server through JSON-RPC.

Run `aspire restore` from the AppHost directory to regenerate bindings without
starting the AppHost. Use `aspire run` to run it. Do not edit `.aspire/modules/`;
change your AppHost or integration references instead.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/reference/cli/commands/aspire-config-set/
* https://aspire.dev/reference/cli/commands/aspire-restore/
* https://aspire.dev/extensibility/multi-language-integration-authoring/

## Feedback & contributing

https://github.com/microsoft/aspire

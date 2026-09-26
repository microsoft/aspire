# Aspire.Hosting.CodeGeneration.Rust

Provides Rust AppHost scaffolding and generates Rust bindings for Aspire hosting
APIs. This is tooling for writing the AppHost in Rust, not an integration for
hosting a Rust application.

## Getting started

Rust AppHost support is experimental. Install the Aspire CLI and the Rust
toolchain, including Cargo. In a new directory for your AppHost, enable the
feature and initialize it:

```bash
aspire config set features.experimentalPolyglot:rust true
aspire init --language rust
```

The CLI restores this code-generation package automatically for a Rust AppHost.
Do not install it with `aspire add`.

Edit `apphost.rs` to define your resources. The scaffolded AppHost loads
`.aspire/modules/mod.rs`, and `Cargo.toml` defines the AppHost binary and dependencies.

## Generated bindings

Bindings are generated from Aspire Type System (ATS) metadata in `Aspire.Hosting`
and the hosting integrations configured in `aspire.config.json`. The generated
Rust API calls the .NET AppHost server through JSON-RPC.

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

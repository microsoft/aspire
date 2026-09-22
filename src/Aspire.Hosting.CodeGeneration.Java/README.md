# Aspire.Hosting.CodeGeneration.Java

Provides Java AppHost scaffolding and generates Java bindings for Aspire hosting
APIs. This is tooling for writing the AppHost in Java, not an integration for
hosting a Java or Spring application.

## Getting started

Java AppHost support is experimental. Install the Aspire CLI and JDK 25 or later.
In a new directory for your AppHost, enable the feature and initialize it:

```bash
aspire config set features.experimentalPolyglot:java true
aspire init --language java
```

The CLI restores this code-generation package automatically for a Java AppHost.
Do not install it with `aspire add`.

Edit `AppHost.java` to define your resources using the generated `aspire` package.
The CLI handles compiling the AppHost and generated Java sources when you run it.

## Generated bindings

Bindings are generated from Aspire Type System (ATS) metadata in `Aspire.Hosting`
and the hosting integrations configured in `aspire.config.json`. Generated classes
live under `.aspire/modules/aspire/` and call the .NET AppHost server through JSON-RPC.

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

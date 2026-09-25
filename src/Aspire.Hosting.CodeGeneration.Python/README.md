# Aspire.Hosting.CodeGeneration.Python

Provides Python AppHost scaffolding and generates Python bindings for Aspire
hosting APIs. This is tooling for writing the AppHost in Python, not an
integration for hosting a Python application.

## Getting started

Python AppHost support is experimental. Install the Aspire CLI and Python 3.11
or later. In a new directory for your AppHost, enable the feature and initialize it:

```bash
aspire config set features.experimentalPolyglot:python true
aspire init --language python
```

The CLI restores this code-generation package automatically for a Python AppHost.
Do not install it with `aspire add`.

Edit `apphost.py` to define your resources using the generated `aspire_app` module.
The CLI sets up the AppHost virtual environment and installs its dependencies,
preferring `uv` when available and otherwise using Python's `venv` and `pip`.

## Generated bindings

Bindings are generated from Aspire Type System (ATS) metadata in `Aspire.Hosting`
and the hosting integrations configured in `aspire.config.json`. The generated
module in `.aspire/modules/` calls the .NET AppHost server through JSON-RPC.

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

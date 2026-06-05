# CertificateGeneration (Vendored from ASP.NET Core)

This directory contains code vendored from the ASP.NET Core repository's shared `CertificateGeneration` library.

**Source:** https://github.com/dotnet/aspnetcore/tree/main/src/Shared/CertificateGeneration

**Last synced:** 2026-02-24 from commit [`3a973a5f4d28242262f27c86eb3f14299fe712ba`](https://github.com/dotnet/aspnetcore/commit/3a973a5f4d28242262f27c86eb3f14299fe712ba) — "Fix memory leaks in CertificateManager by improving certificate disposal patterns (#63321)"

## Local modifications

- Replaced `EventSource`-based logging with `ILogger`/`CertificateManagerLogger` wrapper (AOT-compatible)
- Removed static `Instance` pattern; uses `CertificateManager.Create(ILogger)` factory
- Added instance `Log` property backed by `ILogger`
- Changed `GetDescription` and `ToCertificateDescription` from `static` to instance methods
- Removed `catch when (Log.IsEnabled())` filter pattern (incompatible with ILogger)
- Replaced `new X509Certificate2(...)` with `X509CertificateLoader.LoadPkcs12FromFile(...)` (fixes SYSLIB0057)

## Updating

When syncing with upstream, apply the diff from the upstream commit(s) manually, preserving our local modifications listed above.

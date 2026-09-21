# CertificateGeneration (Vendored from ASP.NET Core)

This directory contains code vendored from the ASP.NET Core repository's shared `CertificateGeneration` library.

**Source:** https://github.com/dotnet/aspnetcore/tree/main/src/Shared/CertificateGeneration

**Last synced:** 2026-07-02 from commit [`fa8126f62f64eaf37292ff1e334ace99bc757bcf`](https://github.com/dotnet/aspnetcore/commit/fa8126f62f64eaf37292ff1e334ace99bc757bcf) — "Fix typos in code. (#67428)"

**Proactive ports:** [dotnet/aspnetcore#69299](https://github.com/dotnet/aspnetcore/pull/69299) at commit [`a77e141a0e79403a7bc1042e34f9fed603fc24f5`](https://github.com/dotnet/aspnetcore/commit/a77e141a0e79403a7bc1042e34f9fed603fc24f5) — "Handle spaces in NSS database arguments"

## Local modifications

- Replaced `EventSource`-based logging with `ILogger`/`CertificateManagerLogger` wrapper (AOT-compatible)
- Removed static `Instance` pattern; uses `CertificateManager.Create(ILogger)` factory
- Added instance `Log` property backed by `ILogger`
- Changed `GetDescription` and `ToCertificateDescription` from `static` to instance methods
- Removed `catch when (Log.IsEnabled())` filter pattern (incompatible with ILogger)
- Replaced `new X509Certificate2(...)` with `X509CertificateLoader.LoadPkcs12FromFile(...)` (fixes SYSLIB0057)
- Adapted .NET 11 `Process.Run` and `StandardOutputHandle` usage to `CertificateProcessRunner`, which concurrently drains redirected output on .NET 10
- Retained support for both the HRESULT and raw Win32 error-code forms of Windows trust cancellation
- Added `ASPIRE_CLI_DEV_CERTS_NSSDB_PATHS` as an Aspire-first alias for `DOTNET_DEV_CERTS_NSSDB_PATHS`

## Updating

When syncing with upstream, apply the diff from the upstream commit(s) manually, preserving our local modifications listed above.

# C++ hello Aspire playground

This playground demonstrates hosting a C++ HTTP API built with CMake and vcpkg from an Aspire AppHost.

## Prerequisites

- CMake
- Ninja
- A C++ compiler toolchain
- vcpkg with `VCPKG_ROOT` set to the root of the vcpkg installation

## Run

```bash
aspire start --apphost CppHello.AppHost/CppHello.AppHost.csproj
aspire wait cpp-api --apphost CppHello.AppHost/CppHello.AppHost.csproj
```

The C++ API reads the `PORT` environment variable provided by Aspire and exposes `/` and `/health`.

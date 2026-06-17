# Set up your environment

Whether you want to contribute to Aspire, or just use the latest build of Aspire, these are the common pieces you need to install.

## Install a supported container runtime

### Docker Desktop
* [Windows](https://docs.docker.com/desktop/install/windows-install/)
* [MacOS X](https://docs.docker.com/desktop/install/mac-install/)
* [Linux](https://docs.docker.com/desktop/install/linux-install/)

### Podman
* [Windows](https://podman.io/docs/installation#windows)
* [macOS](https://podman.io/docs/installation#macos)
* [Linux](https://podman.io/docs/installation#installing-on-linux)

Then you can use either VS Code, Visual Studio, or Codespaces:

## Using VS Code with DevContainers

On Windows, Linux, or Mac you can use VS Code with the DevContainers extension. Currently it's only tested with Docker Desktop.

> :warning: This will use around 16GB of RAM, after you loaded the solution.

#### Install VS Code with DevContainers Extension

* [VS Code](https://code.visualstudio.com/Download)
* [DevContainers Extension](https://marketplace.visualstudio.com/items?itemName=ms-VSCode-remote.remote-containers)

Then choose "Open Folder In Container", choose the root of your cloned repo, then choose "Aspire - Contribute".

## Using Visual Studio

To use Visual Studio, ensure you have [Visual Studio 2022 version 17.14](https://visualstudio.microsoft.com/vs) or later.

When you install, ensure that `ASP.NET and web development` workload is checked.

## Using Codespaces

In your browser, start a Codespace in your fork. The initialization of the code space takes around 5 mins. After that you can open the solution.
This will take on the free version of Codespace around 10 mins.

> :warning: With the free version of Codespaces the development experience can be less than ideal. We recommend using at least a Codespace with 16GB of RAM or use your local VS Code / DevContainers instance.

## Alpine Linux

To build the Aspire repo on Alpine Linux, you'll need to use musl-compatible gRPC tooling. The `Grpc.Tools` package that Aspire uses to generate gRPC interfaces depends on native binaries, but it doesn't include a musl-specific build.

On Alpine Linux, the `grpc-plugins` package includes the necessary binaries. You can install it with:

```bash
apk add --no-cache grpc-plugins
```

Then, override the default `Grpc.Tools` binary paths to point to your musl-compatible gRPC binaries by setting the following environment variables:

```bash
export PROTOBUF_PROTOC=/usr/bin/protoc
export GRPC_PROTOC_PLUGIN=/usr/bin/grpc_csharp_plugin
```

With that, you can build and run the Aspire repo on Alpine Linux.

> :warning: Aspire currently only directly supports the x64/amd64 architecture for Alpine/musl. If you want to build or run Aspire in Alpine on arm64, you may need to use an arm64/x64 compatibility layer like `qemu`.
>
> :warning: Alpine Linux support was added in [this commit](https://github.com/microsoft/aspire/commit/cc2706a90848deec90aa166054e1b2a4ecf94689) and isn't supported in earlier releases. Additionally, Alpine Linux is not currently part of our CI test suite.
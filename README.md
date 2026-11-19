# Ina

**Ina** (**I**L to **NA**tive) compiles a .NET application into a native executable
*without a native toolchain*: no clang, gcc, `ld`, MSVC or sysroot of the target
system is needed, which makes cross compilation a matter of naming the target.

It plugs into `dotnet publish`, where it takes the place of NativeAOT's native link:

```console
$ dotnet publish -r linux-arm64        # a project with <PublishIna>true</PublishIna>, on an x64 machine
  App -> /work/App/bin/Release/net10.0/linux-arm64/App.dll
  Generating native code
  Linked App with Ina (1.4 MB, linux-arm64, glibc 2.17+)
  App -> /work/App/bin/Release/net10.0/linux-arm64/publish/
```

and it is a command of its own for an assembly that is already built:

```console
$ dotnet build -c Release samples/HelloWorld
$ ina samples/HelloWorld/bin/Release/net10.0/HelloWorld.dll --os linux --arch x64
HelloWorld.dll -> /work/HelloWorld (1.2 MB, linux-x64, .NET 10.0.15, glibc 2.17+, compile 1.3s, link 0.13s)
$ ./HelloWorld
Hello, World!
```

> **Status: proof of concept.** Linux executables for x64 and arm64 work, including
> cross compilation between the two. See [Status](#status) for what is and is not covered.

## How it works

Ina borrows code generation from .NET itself and replaces the one step that ties
NativeAOT to a native toolchain: the link.

```
App.dll + dependencies
   │
   │  1. resolve packs    ILCompiler (host) and the NativeAOT runtime pack (target),
   │                      reused from the NuGet cache or downloaded from nuget.org
   ▼
   │  2. compile          ILCompiler (RyuJIT) turns IL into an object file for the target
   ▼
App.o + libRuntime.WorkstationGC.a + libSystem.Native.a + ...
   │
   │  3. link             Ina.Linker, a linker written in C#, produces the executable
   ▼
App  (ELF, dynamically linked against the C library only)
```

With `dotnet publish`, steps 1 and 2 are those of the .NET SDK: NuGet restores the packs
and the NativeAOT targets run ILCompiler. With the `ina` command, Ina does them itself.
Step 3 is the same linker in both cases.

The linker does not need the C library of the target either. It links against an
*import table*: the names and versions of the symbols that glibc exports, generated
once from the oldest glibc that .NET supports. The startup code that a toolchain
takes from `crt1.o` and the handful of functions from `libc_nonshared.a` are generated
by the linker. This is the same idea that lets Go cross compile out of the box.

A side effect is portability: an executable built on a current distribution only
requires the glibc 2.27 baseline (in practice symbols up to 2.17), whereas the same
application published with NativeAOT on Ubuntu 24.04 requires glibc 2.34.

[docs/Design.md](docs/Design.md) explains the design, the alternatives that were
considered and the internals of the linker.

## Usage

Requirements: the .NET 10 SDK, and access to nuget.org the first time a target is
used (the packs are cached afterwards).

The application is compiled the way `PublishAot` compiles it, so the same rules apply
to both ways of using Ina: no runtime code generation, reflection only where the compiler
can see it, and `System.Text.Json` through source generation (see `samples/JsonReader`).

### Publishing a project with `dotnet publish`

`Ina.Sdk` is an MSBuild project SDK that is used in addition to the .NET SDK. A project
refers to it and sets `PublishIna`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Ina.Sdk" Version="0.1.0" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishIna>true</PublishIna>
  </PropertyGroup>

</Project>
```

```console
$ dotnet publish                       # for this machine
$ dotnet publish -r linux-arm64        # for another architecture
```

The publish directory contains the executable and the content files of the project, as
with NativeAOT. `dotnet build` and `dotnet run` keep running the application on the
regular runtime.

`PublishIna` implies `PublishAot`, and that is the whole idea: the .NET SDK does what it
does for NativeAOT (analyzers, trimming, ILCompiler, the properties documented for
[Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/))
and Ina links the result. An application of `Microsoft.NET.Sdk.Web` is published the same
way (see `samples/MinimalApi`).

- The version of the SDK can also be given in `global.json`
  (`"msbuild-sdks": { "Ina.Sdk": "0.1.0" }`), with `<Sdk Name="Ina.Sdk" />` in the project.
- `PublishIna` can come from the command line instead: `dotnet publish -p:PublishIna=true`.
  `enable` is accepted as well as `true`.
- **As a package reference**, for example with central package management, `PublishAot`
  has to be set too. A package takes no part in the restore of the project that refers to
  it, so it cannot make the .NET SDK download the compiler:

  ```xml
  <PropertyGroup>
    <PublishAot>true</PublishAot>
    <PublishIna>true</PublishIna>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Ina.Sdk" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
  ```

What differs from a NativeAOT publish, apart from not needing a toolchain:

| Setting | With `PublishIna` |
|---|---|
| `RuntimeIdentifier` (`-r`) | `linux-x64` or `linux-arm64`, independent of the machine that publishes (tested on a `linux-x64` host only). Anything else is an error (INA1002) |
| `PositionIndependentExecutable` | `false` produces a fixed-address executable |
| `StripSymbols` | `false` keeps the symbol table. There is no debug information and no `.dbg` file yet |
| `NativeLibrary`, `DirectPInvoke` | Static libraries are linked into the executable, as long as they need nothing but the C library |
| `LinkerArg` | Ignored with a warning (INA1005): no native linker runs |
| `NativeLib`, `CustomNativeMain`, `StaticExecutable`, `StaticICULinking`, `StaticOpenSslLinking`, `LinkStandardCPlusPlusLibrary`, `UseSystemZlib`, `UseSystemBrotli` | Not supported (error INA1003) |

`Ina.Sdk` is not on nuget.org. To try it, pack it and add the folder as a package source:

```console
$ dotnet pack src/Ina.Sdk -c Release -o artifacts
$ dotnet nuget add source /path/to/Ina/artifacts --name ina
```

### Compiling an assembly with the `ina` command

```console
$ dotnet build                                   # builds ina into src/Ina/bin
$ dotnet src/Ina/bin/Debug/net10.0/ina.dll --help
```

`ina` can also be packaged and installed as a local .NET tool (a package of about 450 KB):

```console
$ dotnet pack src/Ina -c Release -o artifacts
$ cd ../MyApp && dotnet new tool-manifest
$ dotnet tool install Ina --add-source ../Ina/artifacts --version 0.1.0
$ dotnet ina bin/Release/net10.0/MyApp.dll
```

`ina` takes the application assembly as produced by `dotnet build` (framework-dependent
output is what you want; the dependencies next to it are picked up automatically).

```
ina <assembly> [options]

  -o, --output <path>         Path of the executable [default: the assembly name in the current directory]
  --os <linux>                Target operating system [default: linux]
  --arch <x64|arm64>          Target architecture [default: the architecture of this machine]
  -O, --optimize <mode>       blended, speed, size or none [default: blended]
  --runtime-version <ver>     Version of the .NET runtime packs [default: newest patch of the app's framework]
  --invariant-globalization   Do not depend on ICU at run time
  --symbols                   Keep a symbol table in the executable
  --no-pie                    Produce a fixed-address executable
  --intermediate-dir <dir>    Directory for the object file and the compiler response file
  -v, --verbose               Show phases, timings and compiler output
```

Cross compiling for arm64 on an x64 machine:

```console
$ ina bin/Release/net10.0/App.dll --arch arm64 -o App-arm64
App.dll -> /work/App-arm64 (1.3 MB, linux-arm64, .NET 10.0.15, glibc 2.17+, compile 1.2s, link 0.12s)
```

Environment variables: `INA_PACKS` (pack cache, default `~/.ina/packs`),
`INA_NUGET_SOURCE` (NuGet v3 flat container to download packs from),
`NUGET_PACKAGES` (the NuGet global packages folder is reused read-only).

The command knows the base class library only. Applications that need a shared framework
such as ASP.NET Core are published with `dotnet publish`.

## Status

| | |
|---|---|
| Targets | `linux-x64`, `linux-arm64` (glibc), position independent or fixed-address executables |
| Hosts | Anywhere ILCompiler runs. Developed and tested on `linux-x64`; other hosts are untested |
| Frameworks | .NET 10 and later (the NativeAOT runtime pack layout introduced with .NET 10) |
| Verified | Console apps, multi-assembly apps, `System.Text.Json`, exceptions, GC (workstation and server), threads and tasks, thread statics, file I/O, sockets, loopback HTTP, compression, crypto through OpenSSL. With `dotnet publish` also an ASP.NET Core minimal API |
| Self-hosting | `ina` compiles itself: the native `ina` builds applications, and rebuilding itself with it yields a byte-identical executable |

Not done yet:

- Windows (PE/COFF) and macOS (Mach-O) targets, and fully static musl executables
- Debug information in the executable (the compiler's DWARF output is dropped)
- Native libraries (`NativeLib`) as output
- With the `ina` command: satellite resource assemblies, shared frameworks such as ASP.NET Core,
  and native libraries of NuGet packages. With `dotnet publish` the .NET SDK takes care of
  these inputs: ASP.NET Core is covered by a test, satellite assemblies and a static
  library were tried by hand

Verification gaps are listed in [docs/Design.md](docs/Design.md#7-verification).

## Repository

| Path | Content |
|---|---|
| `src/Ina` | The `ina` command line tool |
| `src/Ina.Sdk` | The integration into `dotnet publish`: MSBuild project SDK and package, link task |
| `src/Ina.Core` | Build pipeline of the command: pack resolution, ILCompiler driver, link step |
| `src/Ina.Linker` | The ELF linker (x86-64 and AArch64) and the C library import tables |
| `samples` | Applications used as end-to-end test subjects |
| `tests` | Unit tests of the linker, the pipeline and the link task; end-to-end tests over the samples |
| `eng` | Engineering scripts, e.g. regenerating the import tables |
| `docs` | [Design](docs/Design.md) and [project guidelines](docs/Guidelines.md) |

## Development

```console
$ dotnet build
$ dotnet test          # ~180 tests, about a minute
```

The tests do not use a native toolchain: object files are assembled in memory by a
test helper, linked, and executed. The tests of the `dotnet publish` integration install
the `Ina.Sdk` package that the build has just produced and publish the samples with a
search path that contains nothing but `dotnet` and `sh`. Tests that run arm64 executables
use `qemu-aarch64` with the C library from `/usr/aarch64-linux-gnu` when both exist
(Debian/Ubuntu: `apt install qemu-user libc6-arm64-cross`) and are skipped otherwise.

The first run of the end-to-end tests downloads the ILCompiler and runtime packs, and
the runtime pack of ASP.NET Core.

## Third-party components

Ina does not redistribute any of these; they are downloaded from nuget.org when needed.

- [ILCompiler](https://www.nuget.org/packages/runtime.linux-x64.Microsoft.DotNet.ILCompiler)
  and the [NativeAOT runtime packs](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64)
  (.NET Foundation, MIT)
- The link task is compiled against [Microsoft.Build.Utilities.Core](https://www.nuget.org/packages/Microsoft.Build.Utilities.Core)
  (Microsoft, MIT); at build time MSBuild provides its own copy

The import tables in `src/Ina.Linker/Abi/Tables` list symbol names and symbol versions
exported by the GNU C library 2.27. They are interface facts extracted with
`eng/generate-abi.sh`; no code from the library is included.

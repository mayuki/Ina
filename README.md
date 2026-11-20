# Ina

**Cross-compile .NET to native executables. No native toolchain required.**

```console
C:\App> dotnet publish -r linux-arm64 -p:PublishAot=true
  App net11.0 linux-arm64 failed with 1 error(s) (0.1s)
    …\Microsoft.NETCore.Native.Publish.targets(125,5): error Cross-OS native compilation is not supported.

C:\App> dotnet publish -r linux-arm64 -p:PublishIna=true
  App net11.0 linux-arm64 succeeded (1.3s) → bin\Release\net11.0\linux-arm64\publish\
```

- Build Linux executables on Windows.
- Build Windows executables on macOS.
- No clang, gcc, MSVC, Xcode, SDK or sysroot: just the .NET SDK, like `GOOS=linux go build`.

**Ina** (**I**L to **NA**tive) compiles a .NET application the way NativeAOT does, and
replaces the one step that needs a native toolchain, the link, with linkers written in C#.

> [!NOTE]
> **Status: proof of concept.** Linux, macOS and Windows executables for x64 and arm64 can
> be built from any host. `osx-x64` executables are built and inspected, but have not been
> run on real hardware. See [Status](#status).

## Quick start

Add `Ina.Sdk` to your project file and enable `PublishIna`. Nothing has to be installed
beforehand except the .NET 11 SDK: NuGet restores
[`Ina.Sdk`](https://www.nuget.org/packages/Ina.Sdk) along with the project.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Ina.Sdk" Version="0.2.0" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <PublishIna>true</PublishIna>
  </PropertyGroup>

</Project>
```

Then publish for any target, whatever machine you are on:

```console
C:\App> dotnet publish -r linux-arm64
Restore complete (0.4s)
  App net11.0 linux-arm64 succeeded (1.3s) → bin\Release\net11.0\linux-arm64\publish\

Build succeeded in 1.4s
```

With `-tl:off`, the output also says what was linked:
`Linked App with Ina (1.4 MB, linux-arm64, glibc 2.25+)`.

Supported targets are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and
`win-arm64`. `PublishIna` implies `PublishAot`, so the rules and properties of
[Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
apply; Ina only takes the place of the native link.

### Alternative: the `ina` command

[`Ina`](https://www.nuget.org/packages/Ina) is a standalone .NET tool that compiles an
assembly that is already built, without changing its project file:

```console
$ dotnet tool install --global Ina
$ dotnet build -c Release samples/HelloWorld
$ ina samples/HelloWorld/bin/Release/net11.0/HelloWorld.dll --os linux --arch x64
HelloWorld.dll -> /work/HelloWorld (1.3 MB, linux-x64, .NET 11.0.0, glibc 2.25+, compile 1.3s, link 0.13s)
$ ina samples/HelloWorld/bin/Release/net11.0/HelloWorld.dll --os windows --arch x64
HelloWorld.dll -> /work/HelloWorld.exe (1.1 MB, win-x64, .NET 11.0.0, compile 1.3s, link 0.10s)
$ ina samples/HelloWorld/bin/Release/net11.0/HelloWorld.dll --os macos --arch arm64
HelloWorld.dll -> /work/HelloWorld (1.3 MB, osx-arm64, .NET 11.0.0, macOS 14.0+, compile 0.9s, link 0.14s)
```

The command knows the base class library only; applications that need a shared framework
such as ASP.NET Core are published with `dotnet publish`.

[docs/Usage.md](docs/Usage.md) is the reference for both: configuration variants
(`global.json`, package reference), the settings that differ from a NativeAOT publish, the
options and environment variables of `ina`, and what the executables require of the
target system.

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
App.o / App.obj + the static libraries of the runtime
   │
   │  3. link             Ina.Linker, three linkers written in C#, produces the executable
   ▼
App      (Linux: ELF, dynamically linked against the C library only)
App      (macOS: Mach-O, linked against libraries and frameworks of the system only)
App.exe  (Windows: PE32+, importing system DLLs and the Universal C Runtime only)
```

With `dotnet publish`, steps 1 and 2 are those of the .NET SDK; with the `ina` command,
Ina does them itself. Step 3 is the same linker in both cases.

The linkers do not need the libraries of the target system either. They link against an
*import table* — the names of the symbols that the system libraries export, shipped as
text — and generate what a toolchain would add to every link, such as the startup code
and, for macOS, the ad-hoc code signature. This is the same idea that lets Go cross
compile out of the box.

A side effect is portability: a Linux executable only requires the glibc 2.27 baseline
wherever it is built, and a Windows executable does not need the Visual C++
redistributable.

[docs/Design.md](docs/Design.md) explains the design, the alternatives that were
considered and the internals of the linkers.

## Status

| | |
|---|---|
| Targets | `linux-x64`, `linux-arm64` (glibc); `osx-x64`, `osx-arm64`; `win-x64`, `win-arm64` (Windows 10 and later) |
| Hosts | Anywhere ILCompiler runs. Developed and tested on `linux-x64`, `win-x64` and `osx-arm64` |
| Frameworks | Applications that target .NET 10 or .NET 11. The tool and the SDK themselves run on .NET 11 |
| Verified | Console apps, multi-assembly apps, `System.Text.Json`, exceptions, GC, threads and tasks, file I/O, sockets, HTTPS, compression, crypto. With `dotnet publish` also an ASP.NET Core minimal API |
| Cross compilation | Linux to Windows and Windows to Linux, Linux to macOS, x64 to arm64. Given the same assemblies, Windows executables built on Linux and on Windows are byte-identical, and so are macOS executables built on Linux and on a Mac |
| Self-hosting | `ina` compiles itself for Linux, macOS and Windows |

Not done yet:

- Running `osx-x64` executables on real hardware
- Debug information (DWARF, dSYM, PDB)
- Windows mitigations that need linker support: Control Flow Guard and the CET marker
- Fully static musl executables, universal macOS binaries
- Native libraries (`NativeLib`) as output
- With the `ina` command: shared frameworks, satellite assemblies and native libraries of
  NuGet packages (`dotnet publish` handles these)

[Verification](docs/Design.md#10-verification) in the design document lists precisely what
was verified and how, and what was not; the [roadmap](docs/Design.md#11-roadmap) follows it.

## Development

```console
$ dotnet build
$ dotnet test
```

- [docs/Development.md](docs/Development.md) — testing, and trying the tool and the
  packages from the source tree
- [docs/Releasing.md](docs/Releasing.md) — how a release is made
- [docs/Guidelines.md](docs/Guidelines.md) — repository layout and project rules
- [docs/Design.md](docs/Design.md) — architecture

## Third-party components

Ina does not redistribute any of these; they are downloaded from nuget.org when needed.

- [ILCompiler](https://www.nuget.org/packages/runtime.linux-x64.Microsoft.DotNet.ILCompiler)
  and the [NativeAOT runtime packs](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64)
  (.NET Foundation, MIT)
- For Windows targets additionally [Microsoft.DotNet.ILCompiler](https://www.nuget.org/packages/Microsoft.DotNet.ILCompiler),
  for its list of Windows APIs that may be called directly (.NET Foundation, MIT)
- The link task is compiled against [Microsoft.Build.Utilities.Core](https://www.nuget.org/packages/Microsoft.Build.Utilities.Core)
  (Microsoft, MIT); at build time MSBuild provides its own copy

The import tables in `src/Ina.Linker/Abi/Tables` are interface facts — names of symbols
exported by glibc, by the system libraries of macOS and by the system DLLs of Windows; no
code of any library is included. [docs/Development.md](docs/Development.md#import-tables)
describes where they come from.

## AI usage and policy

Most of the code in this project is designed and implemented with AI coding agents
(Claude Code, Codex, GitHub Copilot). The direction of the project is still decided by
the repository owner, who chooses what goes in and what does not.

## License

Ina is licensed under the [MIT License](LICENSE).

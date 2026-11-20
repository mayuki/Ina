# Using Ina

The reference for the two ways of using Ina: `Ina.Sdk` with `dotnet publish`, and the
`ina` command. The [README](../README.md) has the short version.

Both need the .NET 11 SDK, and access to nuget.org the first time a target is used (the
packs are cached afterwards). The application is compiled the way `PublishAot` compiles
it, so the same rules apply to both: no runtime code generation, reflection only where
the compiler can see it, and `System.Text.Json` through source generation (see
`samples/JsonReader`).

## Publishing a project with `dotnet publish`

Add `<Sdk Name="Ina.Sdk" Version="0.1.0" />` to a project that uses `Microsoft.NET.Sdk`
(or `Microsoft.NET.Sdk.Web`) and set `<PublishIna>true</PublishIna>`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Ina.Sdk" Version="0.1.0" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <PublishIna>true</PublishIna>
  </PropertyGroup>

</Project>
```

Nothing has to be installed beforehand: NuGet restores `Ina.Sdk` from nuget.org along
with the project. Then publish for the current machine or for any supported target:

```console
$ dotnet publish                       # for this machine
$ dotnet publish -r linux-arm64        # for another architecture
$ dotnet publish -r osx-arm64          # for another operating system
$ dotnet publish -r win-x64            # for Windows, without Visual Studio, also on Linux or a Mac
```

The publish directory contains the executable and the content files of the project, as
with NativeAOT. `dotnet build` and `dotnet run` keep running the application on the
regular runtime.

`PublishIna` implies `PublishAot`, and that is the whole idea: the .NET SDK does what it
does for NativeAOT (analyzers, trimming, ILCompiler, the properties documented for
[Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/))
and Ina links the result. An application of `Microsoft.NET.Sdk.Web` is published the same
way (see `samples/MinimalApi`).

### Configuration variants

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

### Differences from a NativeAOT publish

What differs from a NativeAOT publish, apart from not needing a toolchain:

| Setting | With `PublishIna` |
|---|---|
| `RuntimeIdentifier` (`-r`) | `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` or `win-arm64`, independent of the machine that publishes. Anything else is an error (INA1002). [Verification](Design.md#10-verification) lists which combinations of host and target were run, only built or not tried |
| `PositionIndependentExecutable` | `false` produces a fixed-address executable for Linux, and for Windows one without base relocations. macOS has none, so there it is an error (INA1003) |
| `StripSymbols` | `false` keeps the symbol table of Linux and macOS executables. There is no debug information, and no `.dbg` file, `.dSYM` bundle or `.pdb` file yet |
| `NativeLibrary`, `DirectPInvoke` | Static libraries are linked into the executable, as long as they need nothing but what the import table of the target lists: the C library, for macOS the frameworks that the .NET runtime itself uses, for Windows the system DLLs that it uses and the Universal C Runtime |
| `OutputType`, `LinkerSubsystem`, `IlcDefaultStackSize` | Honored for Windows: `WinExe` produces a graphical application. A subsystem other than `CONSOLE` or `WINDOWS`, or one with a version, is an error (INA1003) |
| `ControlFlowGuard`, `CETCompat` | The linker does not write the tables of Control Flow Guard, so `Guard` is an error (INA1003). Executables are not marked as compatible with CET shadow stacks, whatever `CETCompat` says |
| `LinkerArg` | Ignored with a warning (INA1005): no native linker runs |
| `NativeLib`, `CustomNativeMain`, `StaticExecutable`, `StaticICULinking`, `StaticOpenSslLinking`, `LinkStandardCPlusPlusLibrary`, `UseSystemZlib`, `UseSystemBrotli` | Not supported (error INA1003) |

All diagnostics are listed under [Checks](Design.md#64-checks) in the design document.

## Compiling an assembly with the `ina` command

`ina` compiles an assembly that is already built, without changing its project file. It is
installed as a .NET tool (a package of about 680 KB), either for the user:

```console
$ dotnet tool install --global Ina
$ ina --help
```

or as a local tool of a repository, which records the version in the tool manifest of
the repository:

```console
$ dotnet new tool-manifest                       # if the repository has none yet
$ dotnet tool install Ina
$ dotnet ina bin/Release/net11.0/MyApp.dll
```

It takes the application assembly as produced by `dotnet build` (framework-dependent
output is what you want; the dependencies next to it are picked up automatically).

```
ina <assembly> [options]

  -o, --output <path>         Path of the executable [default: the assembly name in the current directory,
                              with .exe for Windows]
  --os <linux|macos|windows>  Target operating system [default: the system of this machine if it is one of
                              them, otherwise linux]
  --arch <x64|arm64>          Target architecture [default: the architecture of this machine]
  -O, --optimize <mode>       blended, speed, size or none [default: blended]
  --runtime-version <ver>     Version of the .NET runtime packs [default: newest patch of the app's framework]
  --invariant-globalization   Do not depend on ICU at run time
  --symbols                   Keep a symbol table in the executable (not for Windows)
  --no-pie                    Produce a fixed-address executable (not for macOS)
  --intermediate-dir <dir>    Directory for the object file and the compiler response file
  -v, --verbose               Show phases, timings and compiler output
```

Cross compiling, for another architecture and for another operating system:

```console
$ ina bin/Release/net11.0/App.dll --arch arm64 -o App-arm64
App.dll -> /work/App-arm64 (1.3 MB, linux-arm64, .NET 11.0.0, glibc 2.25+, compile 1.2s, link 0.12s)
$ ina bin/Release/net11.0/App.dll --os windows
App.dll -> /work/App.exe (1.1 MB, win-x64, .NET 11.0.0, compile 1.2s, link 0.10s)
$ ina bin/Release/net11.0/App.dll --os macos --arch x64 -o App-mac
App.dll -> /work/App-mac (1.3 MB, osx-x64, .NET 11.0.0, macOS 14.0+, compile 1.2s, link 0.12s)
```

An application that was built as a graphical Windows application (`OutputType=WinExe`)
becomes an executable for the Windows subsystem, which starts without a console window.

### Environment variables

| Variable | Meaning |
|---|---|
| `INA_PACKS` | Pack cache, default `~/.ina/packs` |
| `INA_NUGET_SOURCE` | NuGet v3 flat container to download packs from |
| `NUGET_PACKAGES` | The NuGet global packages folder, which is reused read-only |

### Limitations

The command knows the base class library only. Applications that need a shared framework
such as ASP.NET Core, satellite resource assemblies or native libraries of NuGet packages
are published with `dotnet publish`, where the .NET SDK takes care of these inputs.

## What the executables require

| Target | Requirement |
|---|---|
| Linux | glibc 2.27 as the baseline (in practice symbols up to 2.25). The same application published with NativeAOT on Ubuntu 24.04 requires glibc 2.34 |
| macOS | The system that the runtime was compiled for: macOS 12 with .NET 10, macOS 14 with .NET 11 |
| Windows | Windows 10 or later. The executable depends on nothing but DLLs that are part of Windows; it does not need the Visual C++ redistributable |

macOS executables are signed ad hoc by the linker, which is all macOS asks of a program
that is run from a terminal, so they run on the Mac they are copied to without further
steps. An executable that is built on Windows has to be made executable (`chmod +x`) after
it has been copied to the Mac, because Windows file systems do not record that.

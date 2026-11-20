# Developing Ina

The layout of the repository and the project rules are in [Guidelines.md](Guidelines.md),
the architecture in [Design.md](Design.md), and the release procedure in
[Releasing.md](Releasing.md).

## Building and testing

```console
$ dotnet build
$ dotnet test          # ~470 tests, about two minutes
```

The tests do not use a native toolchain: object files are assembled in memory by test
helpers, linked, and executed. The tests of the `dotnet publish` integration install
the `Ina.Sdk` package that the build has just produced and publish the samples with a
search path that contains nothing but `dotnet` and `sh`. Every test builds and inspects
its executable on any host. What can be executed depends on the machine:

| Executables for | are run |
|---|---|
| `linux-x64` | on x64 Linux; the linker tests also on Windows through WSL when a distribution is installed |
| `linux-arm64` | on arm64 Linux, or with `qemu-aarch64` and the C library from `/usr/aarch64-linux-gnu` (Debian/Ubuntu: `apt install qemu-user libc6-arm64-cross`) |
| `osx-arm64`, `osx-x64` | on a Mac of the same architecture; x64 ones also on Apple silicon with Rosetta |
| `win-x64` | on x64 Windows |
| `win-arm64` | on Windows on ARM64; everywhere else the code that the linker generates runs in an emulator that is part of the tests |

Tests whose executables can not be run are skipped; the tests that inspect the structure
of the produced images run everywhere. The first run of the end-to-end tests downloads the
ILCompiler and runtime packs, and the runtime pack of ASP.NET Core.

`.github/workflows/ci.yaml` runs the test suite on Linux and Windows machines of GitHub. It
also builds the samples for Windows on Linux, runs them on Windows, and checks that a build
of the same assemblies on Windows yields the same bytes; samples published for Windows with
`dotnet publish` on Linux are run on Windows as well. The jobs on arm64 machines, which
include running `win-arm64` executables, are enabled for public repositories; a private
repository opts in with the repository variable `ARM64_RUNNERS=true`. No job runs on a Mac:
macOS executables are built and inspected on the Linux and Windows machines, not run.

[Testing](Design.md#9-testing) in the design document describes what each test project
checks.

## Trying the tool and the packages from the source tree

The steps below are for trying unreleased builds from a local checkout; normal use
installs released packages from nuget.org (see the [README](../README.md)).

`ina` runs straight from the build output:

```console
$ dotnet build                                   # builds ina into src/Ina/bin
$ dotnet src/Ina/bin/Debug/net11.0/ina.dll --help
```

To try the packages as they will be released, pack them into a folder and add the folder
as a package source. Give them a version that no release uses, such as one with a
pre-release label: NuGet keeps every version it has restored in the global packages folder
and uses that copy from then on, whichever source it came from.

```console
$ dotnet pack src/Ina.Sdk -c Release -o artifacts -p:PackageVersion=0.1.0-dev
$ dotnet pack src/Ina -c Release -o artifacts -p:PackageVersion=0.1.0-dev
$ dotnet nuget add source /path/to/Ina/artifacts --name ina
```

A project then refers to `<Sdk Name="Ina.Sdk" Version="0.1.0-dev" />`, and the tool is
installed with `dotnet tool install Ina --version 0.1.0-dev`. For the same reason, a
package that is packed again after a change needs another version, or the old one has to
be removed from the global packages folder (`dotnet nuget locals global-packages --list`
shows where it is).

## Trying `Ina.Sdk` without packaging it

The MSBuild files in `src/Ina.Sdk` are laid out as in the package, so a project can be
published with them straight from the source tree. Two properties take the place of the
reference to the package: one makes MSBuild import `Sdk.props`, the other points to the
link task in the build output instead of the `tasks` folder of the package.

```console
$ dotnet build src/Ina.Sdk
$ dotnet publish samples/MinimalApi -r linux-x64 \
    -p:PublishIna=true \
    -p:CustomAfterMicrosoftCommonProps=$PWD/src/Ina.Sdk/Sdk/Sdk.props \
    -p:InaTasksAssembly=$PWD/src/Ina.Sdk/bin/Debug/net11.0/Ina.Sdk.dll
$ samples/MinimalApi/bin/Release/net11.0/linux-x64/publish/MinimalApi
```

No file has to be changed for this, and the output goes to the `bin` and `obj` directories
of the sample, which Git ignores. Build `src/Ina.Sdk` again after a change to the linker
or the task, and stop the build nodes that MSBuild keeps running (`dotnet build-server
shutdown`, or `MSBUILDDISABLENODEREUSE=1` from the start): a node that ran the task
before keeps using the copy of the linker it loaded then.

A project outside of the repository does the same in its project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Import Project="/path/to/Ina/src/Ina.Sdk/Sdk/Sdk.props" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <PublishIna>true</PublishIna>
    <InaTasksAssembly>/path/to/Ina/src/Ina.Sdk/bin/Debug/net11.0/Ina.Sdk.dll</InaTasksAssembly>
  </PropertyGroup>

</Project>
```

This leaves out how NuGet resolves the SDK and how the package is laid out. The publish
tests cover both with the package itself.

## Import tables

The import tables in `src/Ina.Linker/Abi/Tables` are generated, not written by hand. They
are regenerated with the scripts in `eng`:

| Table | Content | Generated by |
|---|---|---|
| `glibc-*.abi` | Symbol names and symbol versions exported by the GNU C library 2.27 | `eng/generate-abi.sh` |
| `macos-*.abi` | Which system library of macOS exports which symbol: libSystem and the Objective-C runtime completely, the frameworks as far as the .NET runtime refers to them | `eng/generate-macos-abi.sh`, from the text-based stubs (`.tbd`) of Apple's macOS SDK |
| `windows.abi` | Which system DLL of Windows exports which name | `eng/GenerateWindowsAbi.cs`, from the export tables of the DLLs |

The macOS SDK is needed to regenerate the macOS tables, never to use Ina. Neither the
Windows SDK nor the Visual C++ libraries are used, at build time or to generate the table.

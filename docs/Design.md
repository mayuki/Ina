# Ina design

Ina (IL to NAtive) turns a .NET application, given as IL assemblies, into a native
executable. The defining requirement is that this works **without a native toolchain**,
the way `GOOS=linux GOARCH=arm64 go build` does, so that cross compilation needs no
setup beyond choosing the target.

This document describes the design of the proof of concept, why it looks the way it
does, and what is left.

## 1. Goals and non-goals

Goals of the proof of concept:

- Compile console applications that consist of several assemblies and use the real
  base class library (console, files, `System.Text.Json`, ...).
- Produce Linux executables. Windows is desirable later.
- Do not invoke or require a compiler, assembler, linker or sysroot of the target.

Explicitly out of scope for now: optimizing beyond what the borrowed compiler does,
debuggability of the output, and breadth of platform coverage.

## 2. Approach

### 2.1 Where the native toolchain dependency actually is

NativeAOT already compiles IL to machine code without a C compiler: ILCompiler drives
RyuJIT and writes object files with its own object writer. What needs a toolchain is
the last step, which calls `clang` (as a linker driver) to combine

- the object file of the application,
- the static libraries of the runtime (`libRuntime.WorkstationGC.a`, `libSystem.Native.a`, ...),
- the C runtime startup objects (`crt1.o`, `crti.o`, `crtbeginS.o`, ...),
- the static part of the C library (`libc_nonshared.a`) and the compiler support library,
- the shared C library of the *target* system, to resolve symbols and symbol versions.

The last three are why cross compilation needs a sysroot, and the linker is why it needs
a toolchain. Ina therefore keeps ILCompiler and replaces the link step with its own linker.

### 2.2 Alternatives considered

| Approach | Example | Why not (for this proof of concept) |
|---|---|---|
| Own IL compiler, own runtime, own core library | NetWasm | Reaches independence from everything, but running the real BCL (`System.Text.Json`, the console) requires reimplementing most of ILCompiler and the runtime. Far beyond a proof of concept. |
| Translate IL to C++ and compile that | IL2CPP, dn2cpp | Requires a C++ toolchain per target, which is exactly what must be avoided. |
| NativeAOT plus a bundled native linker and sysroot | bflat | Works and cross compiles, but ships `lld` and target libraries as native binaries per host. Ina wants the linking logic to be managed code and the target description to be data. |
| **ILCompiler for code, own managed linker** | **Ina** | Reuses the production compiler and runtime, and owns precisely the part that caused the dependency. |

The trade-off of the chosen approach: Ina depends on the ILCompiler package for the host
(a native executable published for every host .NET supports) and on the precompiled
runtime libraries of the target. Both are ordinary NuGet packages that Ina downloads.

### 2.3 Pipeline

```
ina App.dll --os linux --arch arm64
 │
 ├─ AppAssembly        reads the assembly: entry point, target framework, runtimeconfig.json
 ├─ RuntimePacks       runtime.<host>.Microsoft.DotNet.ILCompiler            -> tools/ilc
 │                     Microsoft.NETCore.App.Runtime.NativeAOT.<target>      -> framework IL + static libraries
 ├─ IlcCompiler        ilc @App.ilc.rsp                                      -> App.o
 └─ ElfLinker          App.o + runtime libraries + import table              -> App
```

`src/Ina.Core` implements the first three steps and `src/Ina.Linker` the last one.
`src/Ina` is the command line front end.

This is the pipeline of the `ina` command, which starts from an assembly. The integration
into `dotnet publish` (section 4) starts from a project: there the .NET SDK resolves the
packs and runs the compiler, and only the last step is Ina's.

**Packs.** The version is the newest patch release of the application's framework
(major.minor from `runtimeconfig.json`, or `TargetFrameworkAttribute`), preferring a
version whose compiler is already on disk. Packages are looked up in the NuGet global
packages folder first and otherwise downloaded into `~/.ina/packs`. Extraction happens
in a staging directory that is renamed at the end, and a marker file distinguishes
complete packages from interrupted ones.

**Compilation.** The ILCompiler command line mirrors what the NativeAOT MSBuild targets
generate for a published console application (`Microsoft.NETCore.Native.targets`): the
application is the compilation root, all other assemblies are references, the feature
switches that `PublishAot` sets by default are applied, and the `configProperties` of the
application's `runtimeconfig.json` override them. The compiler runs as a child process
because the package ships it as a native executable.

**Linking.** See the next section.

## 3. The linker

`Ina.Linker` links ELF relocatable objects and static libraries into a dynamically linked
executable for x86-64 or AArch64. It is not a general purpose replacement for `ld`; it
implements what a NativeAOT link needs, and it reports anything else as an error instead
of producing a questionable image.

### 3.1 Passes

The passes are the methods called by `LinkSession.Run`, in this order:

1. **Startup object.** A synthetic object with the process entry point `_start` is added
   (see 3.3).
2. **Load inputs.** Objects are parsed (`ObjectFile`); archives are indexed (`ArchiveFile`).
   COMDAT groups are deduplicated while parsing: the first group with a signature wins.
   Sections that are not loaded at run time (debug information, comments) are dropped.
3. **Resolve symbols.** Strong definitions override weak ones and tentative (common)
   ones; two strong definitions are an error. Archive members are loaded only when they
   define a symbol that is still undefined, repeatedly until nothing changes.
4. **C library stubs.** Functions that the shared C library does not export are added
   for names that are still undefined (see 3.3).
5. **Linker-defined symbols.** `__init_array_start`, `_GLOBAL_OFFSET_TABLE_`,
   `__dso_handle`, `__start_X`/`__stop_X` for sections whose name is a C identifier, etc.
6. **Garbage collection.** Sections reachable from the entry point, constructors and
   exported symbols are marked live. References to `__start_X`/`__stop_X` keep all
   sections named `X` alive, which is how the runtime finds `__managedcode`, `__modules`
   and `__unbox`.
7. **Relocation scan.** Determines imported symbols, GOT entries, PLT stubs and dynamic
   relocations. An undefined symbol becomes an import if the import table knows it,
   resolves to zero if it is only referenced weakly, and is an error otherwise.
8. **Layout.** Input sections are merged into output sections by name prefix and ordered
   into three loadable segments: read-only (headers, dynamic linking tables, `.rodata`,
   unwind tables), executable, and writable. The writable segment starts with the part
   that is read-only after relocation (`PT_GNU_RELRO`: TLS image, constructors,
   `.data.rel.ro`, `.dynamic`, `.got`), padded to a page boundary.
9. **Write.** Sections are copied and relocations applied; headers, program headers and
   section headers are written.

### 3.2 Unwind information

The .NET runtime finds the unwind information and the exception/GC tables of a managed
frame through `PT_GNU_EH_FRAME`, i.e. the binary search table `.eh_frame_hdr`. The
linker therefore splits every input `.eh_frame` into its CIE and FDE records:

- an FDE is kept only if the code it describes is kept, and unwind records never keep
  code alive;
- a kept FDE keeps what it refers to (its CIE, the language specific data in
  `.dotnet_eh_table` or `.gcc_except_table`);
- the CIE pointer of every FDE is rewritten for its new position;
- `.eh_frame_hdr` is generated from the kept FDEs, sorted by code address.

Exceptions, stack traces and garbage collection in the produced executables depend on
this being right, which is what the `RuntimeTour` sample exercises.

### 3.3 Replacing the pieces of the toolchain

| A native link takes this ... | ... from | Ina |
|---|---|---|
| `_start` | `crt1.o` / `Scrt1.o` | Generated machine code that calls `__libc_start_main` and passes an init function that runs `.init_array`. Passing the init function keeps the image working with glibc before and after 2.34, which changed who runs constructors. |
| `atexit`, `at_quick_exit`, `pthread_atfork` | `libc_nonshared.a` | Generated stubs forwarding to `__cxa_atexit`, `__cxa_at_quick_exit`, `__register_atfork`, only when referenced. |
| `__clear_cache` (AArch64) | `libgcc` / compiler-rt | Generated implementation using the cache maintenance instructions, only when referenced. |
| `__dso_handle`, `__init_array_start`, ... | `crtbegin.o`, linker script | Linker-defined symbols. |
| libc symbols and their versions | `libc.so.6` of the target | The import table (3.4). |
| Constructors of the C runtime | `crti.o`, `crtn.o` | Not needed: `.init_array` is used exclusively. |

### 3.4 Import tables

`src/Ina.Linker/Abi/Tables/glibc-<arch>.abi` describe the shared libraries of the target
as text: for each library and symbol version, the exported functions, data objects and
thread-local variables. They are generated by `eng/generate-abi.sh` from the `libc6`
package of Ubuntu 18.04 (glibc 2.27), the oldest C library that .NET 10 supports.

Consequences:

- A reference binds to the *default* version of a symbol in glibc 2.27, exactly as if the
  link had been done against a 2.27 sysroot. `memcpy` binds to `GLIBC_2.14`,
  `pthread_cond_wait` to `GLIBC_2.3.2`, and nothing can require a newer glibc than the
  baseline. The symbol versions matter: for some functions the oldest version has
  different semantics, so binding without versions would be wrong, not merely old.
- The libraries are listed as dependencies (`DT_NEEDED`) only when a symbol from them is
  used, in the order the official .NET binaries use. Functions such as `pthread_create`
  and `dlopen` lived in `libpthread.so.0` and `libdl.so.2` before glibc 2.34; the
  executable depends on those libraries so that it also loads on old systems, and newer
  systems provide them as empty placeholders.
- A symbol that the table does not know is reported at link time rather than at startup.

Imported functions are called through 8 (x86-64) or 16 byte (AArch64) stubs that jump
through a GOT entry. All GOT entries are bound when the program starts (`BIND_NOW`), so
there is no lazy binding and the GOT can be read-only after relocation.

### 3.5 Thread-local storage

An executable is always TLS module 1 and its thread-local variables have fixed offsets
from the thread pointer. The linker uses this in two ways:

- The access sequences that call the loader are rewritten to thread pointer arithmetic
  when they have the canonical form defined by the ABI: general-dynamic and local-dynamic
  on x86-64 (`X64Target`), TLS descriptors on AArch64 (`Arm64Target`). ILCompiler emits
  these for managed thread statics, and the allocation helpers of the runtime use them,
  so this matters for performance.
- Sequences in any other form keep working: their GOT entries are filled in statically
  with module 1 and the offset, without dynamic relocations.

Local-dynamic offsets are relative to the result of the rewritten call, so on x86-64 the
rewrite happens only if every local-dynamic call site in the image is canonical.

### 3.6 Adding a target

An architecture is a subclass of `TargetArch`: relocation scan and application, the PLT
stub, the entry point and C library stubs as machine code, and the thread pointer offset
formula. A C library is an import table. The runtime identifier of a new target is added
to `TargetPlatform` for the command, and to `SupportedTargets` and
`Ina.Sdk.AfterMicrosoftNETSdk.targets` for `dotnet publish`. Another object format (PE/COFF, Mach-O) is a
larger step: the passes of 3.1 are format-neutral in spirit, but the input model
(`ObjectFile`, `InputSection`) and the output writer are ELF-specific today.

## 4. Integration into `dotnet publish`

`src/Ina.Sdk` makes `dotnet publish` produce the executable with Ina. A project opts in
with `PublishIna`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Ina.Sdk" Version="0.1.0" />
  <PropertyGroup>
    <PublishIna>true</PublishIna>
  </PropertyGroup>
</Project>
```

### 4.1 Approach: NativeAOT up to the link

`PublishIna` is `PublishAot` with a different last step. The .NET SDK does everything it
does for NativeAOT: it restores ILCompiler and the runtime pack with NuGet, enables the
analyzers, infers the runtime identifier, computes the compiler arguments from the
properties and items of the project and runs ILCompiler. Ina then links the object file,
and the publish steps of the .NET SDK copy the result.

This reuses the part of NativeAOT that has no toolchain dependency instead of rebuilding
it, with three consequences. Every setting that is documented for NativeAOT keeps its
meaning without Ina knowing about it. Inputs that the `ina` command does not handle come
for free, because the .NET SDK resolves them: shared frameworks such as ASP.NET Core,
satellite assemblies, static libraries of the project (`NativeLibrary`). And the packs are
restored by NuGet, from the package sources and into the cache that the project uses anyway.

Alternatives that were considered:

| Approach | Why not |
|---|---|
| Run the pipeline of the `ina` command (`Ina.Core`) from a target after the build | It would be independent of the NativeAOT targets, and a package reference would suffice. But the settings of the project would have to be translated into compiler arguments a second time, and so would the publish steps: the .NET SDK treats `PublishAot` specially when it decides what goes into the publish directory (no host, no assemblies, no `deps.json`). Both copies would drift from the original. |
| Leave the NativeAOT targets alone and hand them Ina as their linker (`CppCompilerAndLinker`) | No target would have to be replaced. But Ina would have to understand the command line of clang as these targets happen to produce it, it would need a launcher script per host operating system to be started as a program, and the targets would still look for `objcopy`. |
| Run before `LinkNative` and leave an up-to-date output behind, so that the original target is skipped | It needs no import after the NativeAOT targets, but it relies on time stamps to keep a target from running, and the target that selects the libraries would still fail without a toolchain. |
| **Redefine the targets that need a toolchain, after the NativeAOT targets** | Chosen. The dependency on the internals of those targets is explicit and small (4.3). |

### 4.2 Project SDK or package reference

The integration has to act at two points of the evaluation of a project, and neither is
where a package or a project SDK is imported by default:

```
Sdk.props of Microsoft.NET.Sdk
   └─ build/Ina.Sdk.props                      as a package reference (imported by NuGet)
Sdk/Sdk.props of Ina.Sdk                       as a project SDK; imports build/Ina.Sdk.props
project file                                   <PublishIna>true</PublishIna>
Sdk.targets of Microsoft.NET.Sdk
   ├─ $(BeforeMicrosoftNETSdkTargets)   ──▶    Ina.Sdk.BeforeMicrosoftNETSdk.targets   PublishIna ⇒ PublishAot
   ├─ runtime identifier inference, common targets, publish targets
   ├─ NativeAOT targets of the ILCompiler package (IlcCompile, LinkNative, ...)
   └─ $(AfterMicrosoftNETSdkTargets)    ──▶    Ina.Sdk.AfterMicrosoftNETSdk.targets    checks, replaced targets
Sdk/Sdk.targets of Ina.Sdk                     empty
```

- `PublishIna` has to become `PublishAot` after the project file has set it and before
  the .NET SDK reads `PublishAot` for the first time.
- A target can only be replaced by a definition that comes later, so the replacements
  have to follow the NativeAOT targets, which the .NET SDK imports near its end.

`build/Ina.Sdk.props` therefore does nothing but register two files with the extension
points of the .NET SDK, which import them at exactly these positions. The same file serves
both ways of referring to Ina.

What differs is the restore. The .NET SDK decides *during the restore* whether to download
ILCompiler and the runtime pack, by looking at `PublishAot`. A project SDK is resolved
before any evaluation, including that of the restore, so there `PublishIna` can imply
`PublishAot`. The build files of a package are deliberately left out of the evaluation
that determines what to restore, so a package reference cannot do the same: the project
has to set `PublishAot` itself, and `PublishIna` without it is reported (INA1001).
That makes the project SDK the form to recommend, and the package reference the form
for projects that manage everything through package references.

`PublishIna` accepts `true` and, like `Nullable`, `enable`. The switch is normalized
into a private property because a value given on the command line cannot be rewritten.

### 4.3 What is replaced

`Ina.Sdk.NativeLink.targets` is imported when `PublishIna` is on and the NativeAOT targets
are present. It redefines two targets of `Microsoft.NETCore.Native.Unix.targets` and
`Microsoft.NETCore.Native.targets`:

| Target | Original | Replacement |
|---|---|---|
| `SetupOSSpecificProps` | Selects the runtime libraries to link and the P/Invokes to bind directly; looks for clang or gcc and `objcopy` and fails without them | Selects the same libraries for a dynamically linked glibc executable; looks for nothing |
| `LinkNative` | Runs clang as the linker driver, then `objcopy` to split off the debug information | Runs the `InaLink` task |

Everything before (`IlcCompile` and what it depends on) and after (`ComputeLinkedFilesToPublish`,
`CopyNativeBinary`) is the original. Two properties are set on top: `NativeDebugSymbols`
is turned off, because the linker would drop the debug information that ILCompiler
generates, and `DisableUnsupportedError` is turned on, because the check behind it refuses
to target another operating system for want of a linker.

`InaLink` (`src/Ina.Sdk/InaLink.cs`) receives the object file and the libraries in link
order, the runtime identifier, and the exports file that ILCompiler writes for GNU-style
linkers (a version script naming the symbols that stay visible, e.g.
`DotNetRuntimeDebugHeader`). It runs inside MSBuild; the package brings `Ina.Linker.dll`
along. `Runtime="NET"` on its registration lets an MSBuild that runs on .NET Framework
start the task in a .NET process.

**Incremental builds.** MSBuild skips a target whose outputs are newer than its inputs,
and the options of a link are not files. The target `_InaWriteLinkOptions` writes them
down, next to the response file of ILCompiler, as `obj/.../native/<App>.ina.rsp`, and only
when they differ. Changing `PositionIndependentExecutable`, for example, relinks without
recompiling; the file also shows what exactly was linked.

**Coupling.** The replacement depends on names inside the NativeAOT targets that are not
a documented interface: the two targets, `LinkNativeDependsOn`,
`IlcDynamicBuildPropertyDependencies`, the properties `NativeObject`, `NativeBinary`,
`ExportsFile`, `IlcSdkPath`, `IlcFrameworkNativePath`, `ILCompilerTargetsPath`, and the
items `NativeLibrary` and `DirectPInvoke`; and on the import hooks of the .NET SDK. These
have been stable for several releases, but a new version of .NET can change them, and it
can add a runtime library that then has to be added to the list here as well (the `ina`
command has a list of its own in `BuildPipeline`). The publish tests run against the
installed .NET SDK, so such a change shows up as a failing test.

### 4.4 Checks

The target `_InaValidatePublish` rejects what Ina cannot do with an explanation, instead
of letting the publish fail somewhere in the middle. It runs before the publish steps,
and for a project SDK also before the restore of `dotnet publish`, so that an unsupported
runtime identifier does not first download a compiler and a runtime pack. (A project with
`TargetFrameworks` is restored without a target framework, where the targets of Ina are
not imported; there the check only runs before the publish steps.)

| Code | Reported by | Meaning |
|---|---|---|
| INA1001 | targets | `PublishIna` without `PublishAot` (package reference), or together with `PublishAot=false` |
| INA1002 | targets, task | The runtime identifier is missing or not supported |
| INA1003 | targets | A setting that needs what Ina does not produce or link: `NativeLib`, `CustomNativeMain`, `StaticExecutable`, `StaticICULinking`, `StaticOpenSslLinking`, `LinkStandardCPlusPlusLibrary`, `UseSystemZlib`, `UseSystemBrotli` |
| INA1004 | targets | The application targets a framework before .NET 10 |
| INA1005 | targets (warning) | `LinkerArg` items are ignored, since no native linker runs |
| INA2001 | task | The link failed, e.g. because of an undefined symbol |
| INA2002 | task | An input of the link cannot be read |

### 4.5 The two front ends

| | `ina App.dll` | `dotnet publish` with `PublishIna` |
|---|---|---|
| Input | An assembly that is already built | A project |
| Compiler arguments | Derived by `Ina.Core` from the assembly and its `runtimeconfig.json`, mirroring the NativeAOT targets | Computed by the NativeAOT targets |
| Packs | Downloaded by Ina into `~/.ina/packs`, or reused from the NuGet cache | Restored by NuGet |
| Settings | Options of the command | MSBuild properties of NativeAOT |
| Shared frameworks, satellite assemblies, static libraries | No | Yes |
| Runs with | The .NET runtime, or stand-alone when `ina` is compiled with itself | The .NET SDK |

## 5. Observability

- Every phase of the pipeline is an `Activity` of the `ActivitySource` named `Ina`
  (`ina.build`, `ina.resolve-packs`, `ina.download-package`, `ina.compile`, `ina.link`).
  `ina --verbose` listens to them and prints the durations; any other listener
  (OpenTelemetry, `dotnet-trace`) can do the same.
- Progress, warnings from the compiler and diagnostics go through `ILogger`. The command
  line maps them to plain progress lines, `warning:` and `error:` prefixes.
- The linker returns a `LinkResult` with counts, the dependencies, the required symbol
  versions and the minimum C library version derived from them, which the summary line shows.
- In `dotnet publish` the link task forwards the log of the linker to MSBuild. The summary
  is the one line a normal build shows (`Linked App with Ina (...)`); everything else is of
  low importance and ends up in diagnostic output and binary logs (`-bl`), which also
  record the duration of the task. `obj/.../native/<App>.ina.rsp` lists the inputs and
  options of the last link.

## 6. Testing

| Project | What it checks |
|---|---|
| `Ina.Linker.Tests` | Links small programs that are assembled in memory by `ElfObjectBuilder` and runs them: imports, GOT/PLT, constructors, TLS models, archives, weak and common symbols, COMDAT, garbage collection, unwind tables, image layout. AArch64 relocations are additionally checked by decoding the patched instructions, which needs no AArch64 machine. |
| `Ina.Core.Tests` | ILCompiler arguments, application inspection, pack selection and download against an in-memory package feed. |
| `Ina.Sdk.Tests` | The link task with a recording build engine: linking objects that are assembled in memory, the options, the exports file, and the errors it reports. |
| `Ina.EndToEnd.Tests` | Compiles the samples with the full pipeline, runs them and compares the output with the regular .NET runtime executing the same assembly; drives the `ina` command. Also compiles `ina` itself and uses the native tool to build an application. The publish tests copy a sample, add Ina to its project file the way a user would, and run `dotnet publish`. |

The test suite does not use a native toolchain. Machine code in tests is written as bytes
with the assembly as comments.

The publish tests exercise the package, not the files in the source tree: the build of the
test project packs `Ina.Sdk` into a feed next to the test assembly, under a version label
that is reserved for the tests (`<version>-e2e`), and the tests remove the copy that NuGet
extracted before and after a run, because NuGet never replaces a version it already has.
Every `dotnet publish` of the tests runs with a search path that contains only `dotnet`
and `sh`, so a step that reaches for clang, gcc, `ld` or `objcopy` fails the test.

## 7. Verification

What has been verified, and how:

- `linux-x64`: all samples run natively on the development machine (Ubuntu 24.04, glibc 2.39)
  and behave identically to the regular runtime.
- `linux-arm64`: cross compiled on x64 and run under `qemu-aarch64` with the glibc 2.39
  of Ubuntu's cross packages.
- Self-hosting: `ina` compiled by itself builds the samples (also for arm64), and compiling
  itself again with the native tool produces a byte-identical executable.
- Symbol versions: the samples require at most `GLIBC_2.17` on both architectures, and a
  test checks that nothing newer than the 2.27 baseline is ever required.
- `dotnet publish` (.NET SDK 10.0.404, `linux-x64` host), by tests: as a project SDK and
  as a package reference with central package management; the samples including a project
  reference with a content file and an ASP.NET Core minimal API that serves requests;
  `-r linux-arm64` run under `qemu-aarch64`; the runtime libraries for server GC,
  `EventSourceSupport` and `OptimizationPreference=Speed` (linked and run; event tracing
  itself is not exercised); relinking after a changed link option; the errors of 4.4.
- `dotnet publish`, by hand only: a static library given as `NativeLibrary` with
  `DirectPInvoke` (a C library built with gcc); satellite resource assemblies; the web API
  template of the .NET SDK (`dotnet new webapiaot`); `ina` itself; the version of the SDK
  in `global.json`.

Not verified:

- **Running on old distributions.** The executables bind only to symbols of glibc 2.27,
  but they have not been executed on a system with such an old glibc.
- **Hosts other than linux-x64.** Nothing in Ina is host-specific apart from choosing the
  ILCompiler package, but building on Windows, macOS or arm64 Linux has not been tried.
- **Real arm64 hardware**, in particular kernels with 16 KiB or 64 KiB pages (the image
  is laid out for 64 KiB).
- **Integrity of downloaded packs.** The `ina` command fetches packages over HTTPS from
  nuget.org; their signatures are not validated. (`dotnet publish` restores them with NuGet.)
- **`dotnet publish` on other hosts.** Publishing for Linux from Windows or macOS should
  work, since nothing in the replaced targets runs a shell command, but it has not been
  tried. The same holds for MSBuild on .NET Framework (Visual Studio, `msbuild.exe`), where
  the link task has to run in a .NET task host.
- **Other versions of the .NET SDK.** The replaced targets are internals of the NativeAOT
  targets (4.3); only the SDK named above has been tried.

## 8. Roadmap

Roughly in order of expected value:

1. **Windows targets.** ILCompiler can emit COFF objects on any host, so this is a second
   linker back end: PE/COFF writer, import tables for the Windows DLLs (by name, no import
   libraries needed), and replacements for the pieces of the MSVC runtime that are linked
   statically (startup, security cookie, exception handler registration).
2. **Fully static Linux executables** against musl, for scratch containers. The linker
   already resolves everything statically except the C library; this needs the musl
   runtime pack and musl's `libc.a` as an input.
3. **Debug information**: pass the DWARF sections through and emit a symbol file. With
   `dotnet publish` this is what `StripSymbols` and the `.dbg` file of NativeAOT stand for.
4. **Native libraries as output** (`NativeLib`), which `dotnet publish` could then accept.
5. **Hosting ILCompiler in-process**, which would remove the child process and allow
   writing the executable without an intermediate object file.
6. Smaller items: satellite assemblies and native libraries of NuGet packages for the
   `ina` command, `DT_RELR` packed relocations, deduplication of identical CIEs,
   validation of package signatures, one description of the runtime libraries to link
   for both front ends.

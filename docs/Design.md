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
formula. A C library is an import table. Another object format (PE/COFF, Mach-O) is a
larger step: the passes of 3.1 are format-neutral in spirit, but the input model
(`ObjectFile`, `InputSection`) and the output writer are ELF-specific today.

## 4. Observability

- Every phase of the pipeline is an `Activity` of the `ActivitySource` named `Ina`
  (`ina.build`, `ina.resolve-packs`, `ina.download-package`, `ina.compile`, `ina.link`).
  `ina --verbose` listens to them and prints the durations; any other listener
  (OpenTelemetry, `dotnet-trace`) can do the same.
- Progress, warnings from the compiler and diagnostics go through `ILogger`. The command
  line maps them to plain progress lines, `warning:` and `error:` prefixes.
- The linker returns a `LinkResult` with counts, the dependencies and the required symbol
  versions, from which the summary line derives the minimum C library version.

## 5. Testing

| Project | What it checks |
|---|---|
| `Ina.Linker.Tests` | Links small programs that are assembled in memory by `ElfObjectBuilder` and runs them: imports, GOT/PLT, constructors, TLS models, archives, weak and common symbols, COMDAT, garbage collection, unwind tables, image layout. AArch64 relocations are additionally checked by decoding the patched instructions, which needs no AArch64 machine. |
| `Ina.Core.Tests` | ILCompiler arguments, application inspection, pack selection and download against an in-memory package feed. |
| `Ina.EndToEnd.Tests` | Compiles the samples with the full pipeline, runs them and compares the output with the regular .NET runtime executing the same assembly; drives the `ina` command. Also compiles `ina` itself and uses the native tool to build an application. |

The test suite does not use a native toolchain. Machine code in tests is written as bytes
with the assembly as comments.

## 6. Verification

What has been verified, and how:

- `linux-x64`: all samples run natively on the development machine (Ubuntu 24.04, glibc 2.39)
  and behave identically to the regular runtime.
- `linux-arm64`: cross compiled on x64 and run under `qemu-aarch64` with the glibc 2.39
  of Ubuntu's cross packages.
- Self-hosting: `ina` compiled by itself builds the samples (also for arm64), and compiling
  itself again with the native tool produces a byte-identical executable.
- Symbol versions: the samples require at most `GLIBC_2.17` on both architectures, and a
  test checks that nothing newer than the 2.27 baseline is ever required.

Not verified:

- **Running on old distributions.** The executables bind only to symbols of glibc 2.27,
  but they have not been executed on a system with such an old glibc.
- **Hosts other than linux-x64.** Nothing in Ina is host-specific apart from choosing the
  ILCompiler package, but building on Windows, macOS or arm64 Linux has not been tried.
- **Real arm64 hardware**, in particular kernels with 16 KiB or 64 KiB pages (the image
  is laid out for 64 KiB).
- **Integrity of downloaded packs.** Packages are fetched over HTTPS from nuget.org; their
  signatures are not validated.

## 7. Roadmap

Roughly in order of expected value:

1. **Windows targets.** ILCompiler can emit COFF objects on any host, so this is a second
   linker back end: PE/COFF writer, import tables for the Windows DLLs (by name, no import
   libraries needed), and replacements for the pieces of the MSVC runtime that are linked
   statically (startup, security cookie, exception handler registration).
2. **Fully static Linux executables** against musl, for scratch containers. The linker
   already resolves everything statically except the C library; this needs the musl
   runtime pack and musl's `libc.a` as an input.
3. **`dotnet publish` integration**: an MSBuild package that replaces the `LinkNative`
   step of the NativeAOT targets with Ina's linker, for projects that prefer
   `dotnet publish -r linux-arm64` over a separate command.
4. **Debug information**: pass the DWARF sections through and emit a symbol file.
5. **Hosting ILCompiler in-process**, which would remove the child process and allow
   writing the executable without an intermediate object file.
6. Smaller items: satellite assemblies, native libraries of NuGet packages, `DT_RELR`
   packed relocations, deduplication of identical CIEs, validation of package signatures.

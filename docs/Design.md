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
- Produce Linux and macOS executables. Windows is desirable later.
- Do not invoke or require a compiler, assembler, linker, SDK or sysroot of the target.
  In particular, building for macOS must not require a Mac.

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

For macOS the picture is the same with different names: the link step calls `clang`, which
runs Apple's `ld` with the stubs of the system libraries from the macOS SDK (`.tbd` files),
adds the compiler support library, and has the linker sign the result. Xcode exists only
for macOS, so with the standard tools a macOS executable can only be built on a Mac.

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
 └─ ElfLinker          App.o + runtime libraries + import table              -> App     (Linux)
    MachOLinker                                                                         (macOS)
```

`src/Ina.Core` implements the first three steps and `src/Ina.Linker` the last one.
`src/Ina` is the command line front end.

This is the pipeline of the `ina` command, which starts from an assembly. The integration
into `dotnet publish` (section 5) starts from a project: there the .NET SDK resolves the
packs and runs the compiler, and only the last step is Ina's.

**Targets.** A target is an operating system and an architecture (`TargetPlatform`). The
operating system decides the runtime identifier of the packs (`linux-x64`, `osx-arm64`),
what ILCompiler is told (`--targetos`), which native library implements cryptography
(OpenSSL on Linux, Apple's frameworks on macOS) and which linker is used. Without `--os`
the target is the operating system of the host if Ina can target it, and Linux otherwise.

**Packs.** The version is the newest patch release of the application's framework
(major.minor from `runtimeconfig.json`, or `TargetFrameworkAttribute`), preferring a
version whose compiler is already on disk. Packages are looked up in the NuGet global
packages folder first and otherwise downloaded into `~/.ina/packs`. Extraction happens
in a staging directory that is renamed at the end, and a marker file distinguishes
complete packages from interrupted ones. Builds that start at the same time may each
download a package; the first one to finish installs it and the others discard theirs,
because the installed copy may already be in use.

**Compilation.** The ILCompiler command line mirrors what the NativeAOT MSBuild targets
generate for a published console application (`Microsoft.NETCore.Native.targets`): the
application is the compilation root, all other assemblies are references, the feature
switches that `PublishAot` sets by default are applied, and the `configProperties` of the
application's `runtimeconfig.json` override them. The compiler runs as a child process
because the package ships it as a native executable.

**Linking.** See the next two sections: one linker per object format.

## 3. The ELF linker

`ElfLinker` links ELF relocatable objects and static libraries into a dynamically linked
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
`Ina.Sdk.AfterMicrosoftNETSdk.targets` for `dotnet publish`. Another object format is a
second linker, which is what section 4 describes for Mach-O: the passes are the same in
spirit, but the unit of linking, the relocation model, what the loader expects and what
the toolchain contributes differ too much to share more than the inputs and outputs of a
link.

## 4. The Mach-O linker

`MachOLinker` (`src/Ina.Linker/MachO`) links Mach-O relocatable objects and static
libraries into an executable for macOS on x86-64 or AArch64. Like the ELF linker it
implements what a NativeAOT link needs and reports anything else as an error. It shares
with the ELF linker what is independent of the format: the options and the result of a
link, the archive reader (which understands both the GNU and the BSD dialect of `ar`) and
the import tables. The symbol names in the options are those of C (`main`); the leading
underscore of Mach-O is the linker's business.

### 4.1 Passes

The passes are the methods called by `MachLinkSession.Run`:

1. **Load inputs.** Objects are parsed (`MachObjectFile`) and split into atoms (4.2);
   archives are indexed. Relocations are decoded into one record per reference, because
   Mach-O spreads a reference over several entries and the section content (4.3).
2. **Resolve symbols.** Strong definitions override weak and tentative ones; two strong
   definitions are an error. Archive members are loaded on demand.
3. **Generated functions.** Code that Apple's toolchain adds without being asked is
   created for names that are still undefined (4.5).
4. **Linker-defined symbols.** `__mh_execute_header`, `___dso_handle`, and
   `section$start$SEG$sect` / `section$end$SEG$sect`, through which the .NET runtime finds
   `__managedcode`, `__modules` and `__unbox`.
5. **Dead stripping.** Atoms reachable from `_main`, exported symbols, initializers and
   whatever the object files mark as "no dead strip" are live. A reference to the bounds
   of a section keeps the whole section.
6. **Relocation scan.** Determines imports, GOT entries, stubs and the pointers the loader
   has to fix up. An undefined symbol becomes an import if the import table knows it,
   resolves to null if it is only referenced weakly, and is an error otherwise.
7. **Layout.** Segments `__TEXT` (which starts with the header and the load commands),
   `__DATA_CONST` (what only the loader writes; it is made read-only afterwards), `__DATA`
   and `__LINKEDIT`. Code comes first in `__TEXT` and the unwind tables last; zero-filled
   sections end `__DATA`.
8. **Write.** Sections are copied and relocations applied; the fixup programs, the export
   trie, the symbol table and the function starts go to `__LINKEDIT`, followed by the
   code signature (4.8).

### 4.2 Atoms

Mach-O object files have few, large sections; the unit of linking is the *atom*: the bytes
between two symbols of a section, which an object file that sets "subsections via symbols"
promises to be movable and removable on their own. String literal sections are split at
every string in addition. An atom keeps the alignment that its position in the section
had.

ILCompiler marks every symbol of the application as "no dead strip" and expresses relative
pointers as differences to the start of its sections. The linker therefore never reorders
the atoms of an input section and removes only what an object file allows to be removed,
which in practice is unused code of the native runtime libraries.

### 4.3 Relocations

A relocation entry of a Mach-O object carries no addend. It is in the instruction or data
field, or in a preceding `ADDEND` entry; a difference of two symbols is a `SUBTRACTOR`
entry followed by the entry it modifies; and a reference to a place without a symbol names
the section, with the address of the place inside the object file stored in the field.
The targets (`MachArm64Target`, `MachX64Target`) decode all of that into records of the
form *symbol + addend - subtrahend*, creating unnamed anchor symbols for places that are
given by address. Everything after that works on these records.

Two rewrites make the code cheaper than what the compiler emitted: a load of an address
from the GOT becomes a computation of the address when the symbol is part of the image
(`ldr` to `add`, `mov` to `lea`), and the descriptor of a thread-local variable of the
image is addressed directly.

### 4.4 Unwind information

macOS describes how to unwind in two places. `__unwind_info` is a table that maps code
addresses to a 32-bit *compact encoding*, with a second index for the language specific
data (LSDA) of a function. Encodings that cannot express a frame defer to an FDE in
`__eh_frame`, the DWARF format that ELF uses exclusively. The .NET runtime looks up the
LSDA of a managed method through these tables for every frame it walks, so exceptions,
stack traces and garbage collection depend on them.

Object files contain the raw material: `__LD,__compact_unwind` with one entry per function,
and `__eh_frame`. The linker reads both into *unwind records* that are attached to the
atom of the code they describe (`MachUnwindReader`). Assemblers write the fields of these
tables in several ways (symbol differences, section-relative values, or plain distances
without any relocation on x86-64), so the reader evaluates every field inside the object
file and asks which atom that address belongs to.

- A record stays exactly when its code stays, and keeps what it refers to (LSDA,
  personality function) alive; it never keeps code alive.
- An FDE is emitted only for code whose encoding defers to DWARF, with its fields
  rewritten for the new addresses; the encoding gets the offset of the FDE.
- Code without unwind information gets an explicit "nothing known" entry, so that a lookup
  does not find the entry of the function in front of it.
- `__unwind_info` is written in its compressed form: pages of entries that store the
  distance from the first function of the page and the index of an encoding.

### 4.5 Replacing the pieces of the toolchain

| A native link takes this ... | ... from | Ina |
|---|---|---|
| Process startup | `dyld` and libSystem | Nothing to do: `LC_MAIN` names `main`, and the loader calls it. |
| `_objc_msgSend$selector` | Apple's linker, for Objective-C code compiled with selector stubs | Generated stub that loads the selector and jumps to `objc_msgSend`, with the selector name and the reference the Objective-C runtime fixes up. |
| `___isPlatformVersionAtLeast`, `___isOSVersionAtLeast` (`@available`) | `libclang_rt.osx.a` | Generated functions that ask libSystem (`_availability_version_check`), only when referenced. |
| Symbols of system libraries | `.tbd` stubs of the macOS SDK | The import table (4.6). |
| `section$start$...`, `___dso_handle` | The linker | Linker-defined symbols. |
| The code signature | The linker (ad hoc), or `codesign` | Generated (4.8). |

### 4.6 Import tables

`src/Ina.Linker/Abi/Tables/macos-<arch>.abi` list, for each system library, its install
name, its versions and the symbols it exports. They are generated by
`eng/generate-macos-abi.sh` from the text-based stubs of a macOS SDK
(`MacOSAbiTableGenerator`).

- **libSystem and the Objective-C runtime are listed completely.** Compiled applications
  refer to the C library directly, for example for `Math.Atanh`, so the set of symbols a
  link needs is not known in advance.
- **Frameworks and Swift libraries are listed as far as the native libraries of the .NET
  runtime packs refer to them.** Foundation alone exports tens of thousands of symbols.
  The price is that the tables have to be regenerated when a new .NET version uses more
  of them; the linker then reports the missing symbols by name.
- **A symbol is attributed to the library that has it on the oldest supported system.**
  Apple moves code between libraries and records the former home in the stubs
  (`$ld$previous`). The Swift part of Foundation was `libswiftFoundation.dylib` up to
  macOS 13, so executables name that library, which newer systems keep as a forwarder.
  This is what makes the baseline "macOS 12" rather than "the version of the SDK".

Libraries are dependencies (`LC_LOAD_DYLIB`) only when a symbol from them is used, except
libSystem, which every image must depend on. A library from which only weakly referenced
symbols are used may be missing at run time (`LC_LOAD_WEAK_DYLIB`). A symbol that the
table does not know is reported at link time rather than at startup.

### 4.7 Fixups, stubs and thread-local variables

The loader is told what to patch through the rebase and bind programs of
`LC_DYLD_INFO_ONLY`: pointers into the image, which move with the random load address,
and pointers to imported symbols. This is the older of the two encodings that `dyld`
understands; it is supported by every macOS version and, unlike chained fixups, places no
alignment requirement on the pointers of the object files.

Imported functions are called through stubs (12 bytes on AArch64, 6 on x86-64) that jump
through a GOT entry. All entries are bound when the image is loaded; there is no lazy
binding, and the GOT is in the segment that becomes read-only.

A thread-local variable is a descriptor in `__thread_vars`: a function pointer that the
loader replaces, a key, and the offset of the variable in the per-thread block. The linker
computes the offset relative to the first thread-local data section and lays those
sections out next to each other, because the loader copies the range from the first to
the last as the initial content of a thread's block.

### 4.8 Code signature and reproducibility

macOS on Apple silicon runs only signed code. The linker therefore ends the image with an
*ad-hoc* signature, as Apple's linker does: a code directory with the SHA-256 hash of every
4 KiB page of the file and no identity (`MachCodeSignature`). It needs no certificate and
no tool, and it is what makes building for macOS on other systems possible at all. An
executable that is to be distributed outside of a terminal session (notarization, hardened
runtime, entitlements) is signed again with `codesign` like any other.

The output is a function of the inputs: there are no time stamps, and `LC_UUID`, by which
crash reports and debuggers identify a build, is derived from the content. The output file
is replaced rather than overwritten, because the kernel caches the signature of a file it
has executed.

### 4.9 Adding an architecture

An architecture is a subclass of `MachTarget`: decoding relocation entries, patching
instructions, the stub, and the generated functions as machine code.

## 5. Integration into `dotnet publish`

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

### 5.1 Approach: NativeAOT up to the link

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
| **Redefine the targets that need a toolchain, after the NativeAOT targets** | Chosen. The dependency on the internals of those targets is explicit and small (5.3). |

### 5.2 Project SDK or package reference

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

### 5.3 What is replaced

`Ina.Sdk.NativeLink.targets` is imported when `PublishIna` is on and the NativeAOT targets
are present. It redefines two targets of `Microsoft.NETCore.Native.Unix.targets` and
`Microsoft.NETCore.Native.targets`:

| Target | Original | Replacement |
|---|---|---|
| `SetupOSSpecificProps` | Selects the runtime libraries to link and the P/Invokes to bind directly; looks for clang or gcc and `objcopy` and fails without them | Selects the same libraries for an executable that is linked dynamically against the system libraries only; looks for nothing |
| `LinkNative` | Runs clang as the linker driver, then `objcopy` to split off the debug information (for macOS: `dsymutil` and `strip`) | Runs the `InaLink` task |

Everything before (`IlcCompile` and what it depends on) and after (`ComputeLinkedFilesToPublish`,
`CopyNativeBinary`) is the original. Two properties are set on top: `NativeDebugSymbols`
is turned off, because the linker would drop the debug information that ILCompiler
generates, and `DisableUnsupportedError` is turned on, because the check behind it refuses
to target another operating system for want of a linker.

`InaLink` (`src/Ina.Sdk/InaLink.cs`) receives the object file and the libraries in link
order, the runtime identifier, and the exports file in which ILCompiler names the symbols
that stay visible, e.g. `DotNetRuntimeDebugHeader`. The runtime identifier selects the
linker and its import table: ELF and glibc for Linux, Mach-O and the system libraries of
macOS. The task runs inside MSBuild; the package brings `Ina.Linker.dll` along.
`Runtime="NET"` on its registration lets an MSBuild that runs on .NET Framework start the
task in a .NET process.

**Linux and macOS.** The same two replacements serve both systems, because little of
what differs between them is left in these targets:

- The runtime libraries are the same except for the one that implements cryptography:
  OpenSSL for Linux, Apple's frameworks for macOS.
- The original lists the frameworks and system libraries of a macOS link (`NativeFramework`,
  `NativeSystemLibrary`). Ina's linker does not need the list, since its import table says
  which library exports a symbol, and an image depends only on the libraries it uses.
- The exports file is a version script for GNU-style linkers and a list of symbols, in the
  spelling of object files, for Apple's (`VersionScript`, `ExportedSymbolsList`).
- What the original runs after the link is not needed: the linker strips the image itself
  and signs a macOS executable (4.8). There is no `.dbg` file and no `.dSYM` bundle, as there
  is no debug information.
- A macOS executable is always position independent, so `PositionIndependentExecutable=false`
  is rejected for these targets (5.4).

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

### 5.4 Checks

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
| INA1003 | targets | A setting that needs what Ina does not produce or link: `NativeLib`, `CustomNativeMain`, `StaticExecutable`, `StaticICULinking`, `StaticOpenSslLinking`, `LinkStandardCPlusPlusLibrary`, `UseSystemZlib`, `UseSystemBrotli`, and for macOS `PositionIndependentExecutable=false` |
| INA1004 | targets | The application targets a framework before .NET 10 |
| INA1005 | targets (warning) | `LinkerArg` items are ignored, since no native linker runs |
| INA2001 | task | The link failed, e.g. because of an undefined symbol |
| INA2002 | task | An input of the link cannot be read |

### 5.5 The two front ends

| | `ina App.dll` | `dotnet publish` with `PublishIna` |
|---|---|---|
| Input | An assembly that is already built | A project |
| Compiler arguments | Derived by `Ina.Core` from the assembly and its `runtimeconfig.json`, mirroring the NativeAOT targets | Computed by the NativeAOT targets |
| Packs | Downloaded by Ina into `~/.ina/packs`, or reused from the NuGet cache | Restored by NuGet |
| Settings | Options of the command | MSBuild properties of NativeAOT |
| Shared frameworks, satellite assemblies, static libraries | No | Yes |
| Runs with | The .NET runtime, or stand-alone when `ina` is compiled with itself | The .NET SDK |

## 6. Observability

- Every phase of the pipeline is an `Activity` of the `ActivitySource` named `Ina`
  (`ina.build`, `ina.resolve-packs`, `ina.download-package`, `ina.compile`, `ina.link`).
  `ina --verbose` listens to them and prints the durations; any other listener
  (OpenTelemetry, `dotnet-trace`) can do the same.
- Progress, warnings from the compiler and diagnostics go through `ILogger`. The command
  line maps them to plain progress lines, `warning:` and `error:` prefixes.
- The linker returns a `LinkResult` with counts, the dependencies and what the executable
  requires of the system: symbol versions for ELF, from which the minimum C library version
  is derived, and the minimum version of macOS for Mach-O. The summary line shows that
  requirement.
- In `dotnet publish` the link task forwards the log of the linker to MSBuild. The summary
  is the one line a normal build shows (`Linked App with Ina (...)`); everything else is of
  low importance and ends up in diagnostic output and binary logs (`-bl`), which also
  record the duration of the task. `obj/.../native/<App>.ina.rsp` lists the inputs and
  options of the last link.

## 7. Testing

| Project | What it checks |
|---|---|
| `Ina.Linker.Tests` | Links small programs that are assembled in memory by `ElfObjectBuilder` and `MachObjectBuilder` and runs them: imports, GOT and stubs, constructors, thread-local variables, archives, weak and common symbols, COMDAT, garbage collection and dead stripping, unwind tables, image layout. The images are also read back the way a loader does (`ElfImage`, `MachOImage`: load commands, fixup programs, export trie, unwind table, code signature), and relocations are checked by decoding the patched instructions, so that everything but the execution is tested on every host. For macOS the system unwinder walks a real stack through frames described by compact encodings and by DWARF. |
| `Ina.Core.Tests` | Targets, ILCompiler arguments, application inspection, pack selection and download against an in-memory package feed. |
| `Ina.Sdk.Tests` | The link task with a recording build engine: linking objects that are assembled in memory for both object formats, the options, the exports file in its two forms, and the errors it reports. |
| `Ina.EndToEnd.Tests` | Compiles the samples with the full pipeline for Linux and macOS, runs them where the host can and compares the output with the regular .NET runtime executing the same assembly; drives the `ina` command. Also compiles `ina` itself and uses the native tool to build an application. The publish tests copy a sample, add Ina to its project file the way a user would, and run `dotnet publish` for Linux and for macOS. |

The test suite does not use a native toolchain. Machine code in tests is written as bytes
with the assembly as comments.

The publish tests exercise the package, not the files in the source tree: the build of the
test project packs `Ina.Sdk` into a feed next to the test assembly, under a version label
that is reserved for the tests (`<version>-e2e`), and the tests remove the copy that NuGet
extracted before and after a run, because NuGet never replaces a version it already has.
Every `dotnet publish` of the tests runs with a search path that contains only `dotnet`
and `sh`, so a step that reaches for clang, gcc, `ld` or `objcopy` fails the test, and
likewise one that reaches for Apple's `dsymutil`, `strip` or `codesign`. NuGet's audit is
switched off for these commands: the samples treat warnings as errors, and an advisory for
the compiler package of the installed .NET SDK would fail every publish for a reason that
has nothing to do with Ina.

## 8. Verification

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
  itself is not exercised); relinking after a changed link option; the errors of 5.4.
- `dotnet publish`, by hand only: a static library given as `NativeLibrary` with
  `DirectPInvoke` (a C library built with gcc); satellite resource assemblies; the web API
  template of the .NET SDK (`dotnet new webapiaot`); `ina` itself; the version of the SDK
  in `global.json`.
- `dotnet publish` for Linux on the Mac (.NET SDK 10.0.400, `osx-arm64` host), by the tests
  that do not have to run what they publish: `-r linux-arm64` with the header of the
  executable inspected, relinking after a changed link option, the errors of 5.4.
- `osx-arm64`: all samples run natively on an Apple silicon Mac (macOS 27.2, which was
  also the host of the build) and behave identically to the regular runtime. A larger ad
  hoc program covered what the samples do not: AES-GCM and ChaCha20-Poly1305 (Swift code
  calling CryptoKit), RSA, ECDSA, X.509, TLS and HTTP over loopback, DNS, compression, ICU
  and time zones, child processes. Its output is identical to that of the regular runtime.
- Self-hosting on macOS: `ina` compiled by itself builds the samples, and compiling itself
  again with the native tool produces a byte-identical executable.
- Building macOS executables on Linux: in a virtual machine with Debian 12 (arm64) and
  nothing but the .NET runtime, `ina` built all samples and itself for `osx-arm64` and
  `osx-x64`. Every executable is byte-identical to the one built on the Mac from the same
  inputs and is accepted by Apple's `codesign`; the arm64 ones run on the Mac, including the
  `ina` that was built on Linux, which in turn builds a working application. The tests of
  the linker and of the pipeline and the end-to-end tests of the `ina` command pass on that
  Linux host too, which also covers `linux-arm64` as a host and runs the AArch64 ELF tests
  natively instead of under emulation.
- Independence from Apple's tools on a Mac: the samples were built inside a sandbox in
  which neither Xcode nor the Command Line Tools can be read and nothing in `/usr/bin` can
  be executed. The product code has no macOS-specific code path; the host only decides
  which ILCompiler package is used.
- Comparison with Apple's linker: the same objects were linked with `ld` (as a development
  aid) and compared with Ina's output for both architectures. The disassembly of all
  functions is identical except for padding, the rebase locations match in number per
  section, the imports are bound to the same libraries with the same weakness, every
  entry of `__unwind_info` has the same encoding and LSDA, and Apple's `dwarfdump` finds
  the same FDEs. Apple's `codesign` accepts the signatures.
- `dotnet publish` for macOS (.NET SDK 10.0.400, on the Mac), by tests: `-r osx-arm64` as a
  project SDK, and as a package reference without a runtime identifier, which publishes
  for the Mac itself; the samples including a project reference with a content file and
  the ASP.NET Core minimal API that serves requests; the runtime libraries for server GC,
  `EventSourceSupport` and `OptimizationPreference=Speed`; relinking after a changed link
  option; the rejection of a fixed-address executable. Everything published for `osx-arm64`
  is run. `-r osx-x64` is published and its header inspected.
- Comparison of `dotnet publish` with the link of NativeAOT: the samples were also published
  with `PublishAot` and Apple's toolchain (as a development aid), for both architectures.
  The programs print the same, export the same symbol and import the same symbols of the
  system, with two differences that are intended: the version check of the compiler
  support library is generated (4.5) instead of linked, which saves its imports for reading
  a file, and all imports are bound when the image is loaded, so that an x64 image does
  not need `dyld_stub_binder`. The segments have the same sizes to within a page. The
  comparison needs the same compiler input on both sides: for an application of the web
  SDK that meant setting `EventSourceSupport`, which that SDK only switches on by itself
  when the project sets `PublishAot`.

Not verified:

- **Running `osx-x64` executables.** The development machine has no Rosetta, so x64
  executables were compared with Apple's linker as described above but never run. The
  x64 tests execute on an Intel Mac or a Mac with Rosetta and are skipped elsewhere.
- **Old versions of macOS.** The executables are bound the way Apple's linker binds for
  macOS 12, but they have only been run on macOS 27.
- **Running on old distributions.** The executables bind only to symbols of glibc 2.27,
  but they have not been executed on a system with such an old glibc.
- **Windows hosts.** Building on Windows has not been tried.
- **Real arm64 hardware**, in particular kernels with 16 KiB or 64 KiB pages (the image
  is laid out for 64 KiB).
- **Integrity of downloaded packs.** The `ina` command fetches packages over HTTPS from
  nuget.org; their signatures are not validated. (`dotnet publish` restores them with NuGet.)
- **`dotnet publish` on other hosts.** For Linux targets only the tests named above have
  run on a Mac, and nothing that was published there has been executed. For macOS
  targets `dotnet publish` has only been run on a Mac: its tests publish on every host,
  with nothing but `dotnet` and `sh` on the search path, and the `ina` command has built
  macOS executables on Linux, but the .NET SDK on Linux with a macOS target has not been
  tried. Publishing from Windows should work as well, since nothing in the replaced targets
  runs a shell command, but it has not been tried either. The same holds for MSBuild on
  .NET Framework (Visual Studio, `msbuild.exe`), where the link task has to run in a .NET
  task host.
- **Other versions of the .NET SDK.** The replaced targets are internals of the NativeAOT
  targets (5.3); only the SDKs named above have been tried.

## 9. Roadmap

Roughly in order of expected value:

1. **Windows targets.** ILCompiler can emit COFF objects on any host, so this is a third
   linker back end: PE/COFF writer, import tables for the Windows DLLs (by name, no import
   libraries needed), and replacements for the pieces of the MSVC runtime that are linked
   statically (startup, security cookie, exception handler registration).
2. **Fully static Linux executables** against musl, for scratch containers. The linker
   already resolves everything statically except the C library; this needs the musl
   runtime pack and musl's `libc.a` as an input.
3. **Debug information**: pass the DWARF sections through and emit a symbol file (a dSYM
   bundle for macOS). With `dotnet publish` this is what `StripSymbols` and the `.dbg`
   file of NativeAOT stand for.
4. **Native libraries as output** (`NativeLib`), which `dotnet publish` could then accept.
5. **Hosting ILCompiler in-process**, which would remove the child process and allow
   writing the executable without an intermediate object file.
6. Smaller items: satellite assemblies and native libraries of NuGet packages for the
   `ina` command, `DT_RELR` packed relocations, deduplication of identical CIEs,
   validation of package signatures, one description of the runtime libraries to link
   for both front ends. For macOS: universal binaries (both architectures in one file),
   merging of identical string literals and selector references, and range extension
   thunks for images with more than 128 MiB of AArch64 code.

#!/usr/bin/env bash
# Regenerates the import tables in src/Ina.Linker/Abi/Tables from the C library packages of the oldest
# distribution that the .NET runtime supports. Only symbol names and versions are extracted.
#
# Requires: curl, ar, tar (with xz support) and the .NET SDK. Only needed when the baseline changes.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# glibc 2.27 (Ubuntu 18.04) is the minimum version supported by .NET 10.
glibc_version="2.27"
glibc_package="2.27-3ubuntu1"

generate() {
    local arch="$1" deb_arch="$2" mirror="$3" libdir="$4" loader="$5" interp="$6" description="$7"
    local dir="$work/$arch"
    mkdir -p "$dir"
    curl -fsSL -o "$dir/libc6.deb" "$mirror/pool/main/g/glibc/libc6_${glibc_package}_${deb_arch}.deb"
    (cd "$dir" && ar x libc6.deb && tar xf data.tar.*)

    # A symbol that several libraries export is attributed to the first one listed here, while the "needed" order
    # is the order in which the official .NET binaries list their dependencies.
    dotnet run "$root/eng/GenerateAbi.cs" -- \
        "$root/src/Ina.Linker/Abi/Tables/glibc-$arch.abi" \
        "glibc $glibc_version $description" \
        "$interp" \
        "libdl.so.2,librt.so.1,libm.so.6,libpthread.so.0,libc.so.6,$loader" \
        "libc.so.6=$dir/lib/$libdir/libc.so.6" \
        "libm.so.6=$dir/lib/$libdir/libm.so.6" \
        "libpthread.so.0=$dir/lib/$libdir/libpthread.so.0" \
        "libdl.so.2=$dir/lib/$libdir/libdl.so.2" \
        "librt.so.1=$dir/lib/$libdir/librt.so.1" \
        "$loader=$dir/lib/$libdir/$loader"
}

generate x64 amd64 https://archive.ubuntu.com/ubuntu x86_64-linux-gnu ld-linux-x86-64.so.2 /lib64/ld-linux-x86-64.so.2 x86_64
generate arm64 arm64 https://ports.ubuntu.com/ubuntu-ports aarch64-linux-gnu ld-linux-aarch64.so.1 /lib/ld-linux-aarch64.so.1 aarch64

#!/usr/bin/env bash
# Regenerates the import tables in src/Ina.Linker/Abi/Tables/macos-*.abi from the text-based stubs (.tbd) of a
# macOS SDK and the native libraries of the .NET runtime packs. Only the names of exported symbols and the
# libraries that export them are extracted.
#
# The C library (libSystem) and the Objective-C runtime are listed completely, because compiled applications
# refer to them directly. Frameworks and Swift libraries export tens of thousands of symbols; for those the
# tables list what the native libraries of the runtime packs refer to, so they have to be regenerated when a
# new .NET version starts to use more of them.
#
# Requires: macOS with Xcode or the Command Line Tools (for the SDK), curl, unzip and the .NET SDK. Only needed
# when the .NET runtime or the SDK baseline changes; Ina itself never needs an SDK.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

sdk="$(xcrun --sdk macosx --show-sdk-path)"
sdk_version="$(xcrun --sdk macosx --show-sdk-version)"
frameworks="$sdk/System/Library/Frameworks"

# The runtime packs of .NET 10 are compiled for macOS 12.0 and later.
minimum_os="12.0"
feed="https://api.nuget.org/v3-flatcontainer"

# Dependencies are listed in the order in which the official .NET binaries list them.
needed="/usr/lib/libSystem.B.dylib"
needed+=",/usr/lib/libobjc.A.dylib"
needed+=",/usr/lib/swift/libswiftCore.dylib"
needed+=",/usr/lib/swift/libswiftFoundation.dylib"
needed+=",/usr/lib/libicucore.A.dylib"
needed+=",/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation"
needed+=",/System/Library/Frameworks/CryptoKit.framework/Versions/A/CryptoKit"
needed+=",/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation"
needed+=",/System/Library/Frameworks/Network.framework/Versions/A/Network"
needed+=",/System/Library/Frameworks/Security.framework/Versions/A/Security"
needed+=",/System/Library/Frameworks/GSS.framework/Versions/A/GSS"

generate() {
    local arch="$1" rid="$2"
    local package="microsoft.netcore.app.runtime.nativeaot.$rid"

    # The first and the newest release of the runtime: together they cover what the servicing releases use.
    local versions="${DOTNET_RUNTIME_VERSIONS:-}"
    if [ -z "$versions" ]; then
        local newest
        newest="$(curl -fsSL "$feed/$package/index.json" | grep -o '"10\.0\.[0-9]*"' | tr -d '"' | sort -t. -k3 -n | tail -1)"
        versions="10.0.0 $newest"
    fi

    local directories=""
    for version in $versions; do
        local dir="$work/$rid/$version"
        mkdir -p "$dir"
        curl -fsSL -o "$dir/pack.nupkg" "$feed/$package/$version/$package.$version.nupkg"
        unzip -q -o "$dir/pack.nupkg" "runtimes/$rid/native/*" -d "$dir"
        directories+="${directories:+,}$dir/runtimes/$rid/native"
    done

    # The Swift libraries of the system: the runtime, and the overlays that Swift code asks to be loaded.
    local swift=()
    for stub in "$sdk"/usr/lib/swift/libswift*.tbd; do
        swift+=("ref=$stub")
    done

    # A symbol that several libraries export is attributed to the first one listed here.
    dotnet run "$root/eng/GenerateMacOSAbi.cs" -- \
        "$root/src/Ina.Linker/Abi/Tables/macos-$arch.abi" \
        "$arch" "$minimum_os" "$sdk_version" "$needed" "$directories" \
        "all=$sdk/usr/lib/libSystem.B.tbd" \
        "all=$sdk/usr/lib/libobjc.A.tbd" \
        "ref=$sdk/usr/lib/libicucore.A.tbd" \
        "ref=$frameworks/CoreFoundation.framework/CoreFoundation.tbd" \
        "ref=$frameworks/Foundation.framework/Foundation.tbd" \
        "ref=$frameworks/Security.framework/Security.tbd" \
        "ref=$frameworks/GSS.framework/GSS.tbd" \
        "ref=$frameworks/Network.framework/Network.tbd" \
        "ref=$frameworks/CryptoKit.framework/CryptoKit.tbd" \
        "${swift[@]}"
}

# The generator reports the symbols that none of the libraries exports. Expected are the entry points of the
# managed runtime (defined by the compiled application) and the symbols the linker provides itself
# (section$start$..., ___dso_handle, _objc_msgSend$..., ___isPlatformVersionAtLeast). Anything else points at
# a library that is missing from the list above.
generate arm64 osx-arm64
generate x64 osx-x64

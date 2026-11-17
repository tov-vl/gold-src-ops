#!/usr/bin/env bash
set -Eeuo pipefail
# Build only the pinned module/SDK source list from upstream AMBuilder.
# Caller supplies verified source trees and an empty output directory.
source_root="$1"
amtl_root="$2"
output="$3"
overlay="${4:-}"
flags=()
if [[ "${5:-}" == testing ]]; then flags=(-DNVAULT_TESTING); fi
[[ ! -e "$output" ]]
mkdir -p "$output"
export SOURCE_DATE_EPOCH=1788402491
if [[ -n "$overlay" ]]; then
    mkdir /tmp/nvault-source
    cp -a "$source_root/." /tmp/nvault-source/
    source_root=/tmp/nvault-source
    python3 "$overlay/apply-overlay.py" "$source_root" "$overlay/src"
fi
module="$source_root/modules/nvault"
g++ -m32 -std=c++11 -O2 -fPIC -shared -fno-strict-aliasing -fno-exceptions -fno-rtti \
    -fvisibility=hidden -fvisibility-inlines-hidden -frandom-seed=nvault \
    "-ffile-prefix-map=$source_root=/source/amxmodx" "-ffile-prefix-map=$amtl_root=/source/amtl" \
    -DHAVE_STDINT_H -D_FILE_OFFSET_BITS=64 -D_LINUX -DLINUX "${flags[@]}" \
    -I"$module" -I"$source_root/public/sdk" -I"$source_root/public" \
    -I"$source_root/public/memtools" -I"$amtl_root" -I"$amtl_root/amtl" \
    "$source_root/public/sdk/amxxmodule.cpp" "$module/amxxapi.cpp" \
    "$module/Binary.cpp" "$module/Journal.cpp" "$module/NVault.cpp" \
    -static-libstdc++ -static-libgcc -Wl,--build-id=sha1 -o "$output/nvault_amxx_i386.so"
readelf -h "$output/nvault_amxx_i386.so" > "$output/elf-header.txt"
readelf -d "$output/nvault_amxx_i386.so" > "$output/elf-dynamic.txt"
readelf --version-info "$output/nvault_amxx_i386.so" > "$output/elf-versions.txt"
nm -D --defined-only "$output/nvault_amxx_i386.so" > "$output/exports.txt"
g++ --version > "$output/compiler.txt"
dpkg-query -W > "$output/packages.txt"
sha256sum "$output/nvault_amxx_i386.so"

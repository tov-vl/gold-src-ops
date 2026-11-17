#!/usr/bin/env bash
set -Eeuo pipefail
ulimit -c 0
source_root="$1"
amtl_root="$2"
output="$3"
component=/repo/native/nvault-durability
mkdir -p "$output"
mkdir /tmp/nvault-tests-source
cp -a "$source_root/." /tmp/nvault-tests-source/
source_root=/tmp/nvault-tests-source
python3 "$component/apply-overlay.py" "$source_root" "$component/src"
module="$source_root/modules/nvault"
for mode in plain asan; do
    flags=()
    if [[ "$mode" == asan ]]; then flags=(-fsanitize=address,undefined -fno-omit-frame-pointer); fi
    g++ -m32 -std=c++11 -O1 -g -fno-exceptions -fno-rtti -DNVAULT_TESTING -DHAVE_STDINT_H -D_FILE_OFFSET_BITS=64 \
        -I"$module" -I"$source_root/public" -I"$source_root/public/sdk" -I"$amtl_root" -I"$amtl_root/amtl" \
        "${flags[@]}" "$component/tests/storage.cpp" "$module/NVault.cpp" "$module/Journal.cpp" \
        -o "$output/storage-$mode"
    runuser -u nobody -- env ASAN_OPTIONS=detect_leaks=1:halt_on_error=1 UBSAN_OPTIONS=halt_on_error=1 \
        "$output/storage-$mode" > "$output/$mode.log" 2>&1
    cat "$output/$mode.log"
done
for version in old new; do
    tree=/source
    if [[ "$version" == new ]]; then tree="$source_root"; fi
    module="$tree/modules/nvault"
    g++ -m32 -std=c++11 -O2 -fno-exceptions -fno-rtti -DHAVE_STDINT_H -D_FILE_OFFSET_BITS=64 \
        -I"$module" -I"$tree/public" -I"$tree/public/sdk" -I"$amtl_root" -I"$amtl_root/amtl" \
        "$component/tests/compatibility.cpp" "$module/Binary.cpp" "$module/NVault.cpp" "$module/Journal.cpp" \
        -o "$output/compatibility-$version"
done

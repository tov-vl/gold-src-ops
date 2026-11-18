#!/usr/bin/env bash
set -Eeuo pipefail
# Verified source trees and a fresh output directory are supplied by the caller.
source_root="$1"
amtl_root="$2"
component="$3"
output="$4"
[[ ! -e "$output" ]]
mkdir -p "$output"
cp -a "$source_root" "$output/patched-source"
python3 "$component/apply-overlay.py" "$output/patched-source" "$component/src"
for version in old new; do
    tree="$source_root"
    flags=(-DPLAYER_STORAGE_OLD_READER)
    if [[ "$version" == new ]]; then tree="$output/patched-source"; flags=(); fi
    module="$tree/modules/nvault"
    g++ -m32 -std=c++11 -O2 -fno-exceptions -fno-rtti -DHAVE_STDINT_H -D_FILE_OFFSET_BITS=64 \
        "${flags[@]}" -I"$module" -I"$tree/public" -I"$tree/public/sdk" -I"$amtl_root" -I"$amtl_root/amtl" \
        /repo/tools/native/player-storage-probe.cpp "$module/Binary.cpp" "$module/NVault.cpp" "$module/Journal.cpp" \
        -static-libstdc++ -static-libgcc -o "$output/player-storage-$version"
    readelf -h "$output/player-storage-$version" > "$output/elf-$version.txt"
    if strings "$output/player-storage-$version" | grep -E 'NVAULT_(FAIL|KILL|SLOW_IO)|nvault_test_(clock|failure)' > /dev/null; then exit 1; fi
done
sha256sum "$output"/player-storage-{old,new}

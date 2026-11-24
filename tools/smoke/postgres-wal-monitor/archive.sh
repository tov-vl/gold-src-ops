#!/bin/sh
set -eu

test -f /archive/online
target="/archive/$2"
if test -e "$target"; then
    cmp -s "$1" "$target"
    exit
fi
temporary="$target.tmp"
trap 'rm -f "$temporary"' EXIT
cp "$1" "$temporary"
sync "$temporary"
mv "$temporary" "$target"

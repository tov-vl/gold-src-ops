#!/bin/sh
set -eu
set -o pipefail
export LC_ALL=C

mode="$1"
endpoint="$2"
key="$3"
path="$4"

if [ "$mode" = send ]; then
    digest=$(sha256sum "$path" | cut -d ' ' -f 1)
    length=$(stat -c %s "$path")
    reply=$(
        {
            printf 'POST %s HTTP/1.0\r\nHost: %s\r\nContent-Length: %s\r\nX-Content-SHA256: %s\r\nConnection: close\r\n\r\n' \
                "$key" "$endpoint" "$length" "$digest"
            cat -- "$path"
        } | timeout 15 nc -w 5 "$endpoint" 8080
    )
    status=$(printf '%s\n' "$reply" | head -n 1)
    case "$status" in
        'HTTP/1.0 200 '*|'HTTP/1.0 201 '*) exit 0 ;;
        *) printf 'archive delivery failed: %s\n' "$status" >&2; exit 1 ;;
    esac
fi

test "$mode" = fetch
digest=$(wget -q -Y off -T 3 -O - "http://$endpoint:8080/sha$key")
test "${#digest}" -eq 64
temporary="$path.lab-download"
trap 'rm -f -- "$temporary"' EXIT
wget -q -Y off -T 3 -O "$temporary" "http://$endpoint:8080$key"
if ! printf '%s  %s\n' "$digest" "$temporary" | sha256sum -c - >/dev/null; then
    printf 'archive checksum mismatch for %s\n' "$key" >&2
    exit 1
fi
mv -- "$temporary" "$path"

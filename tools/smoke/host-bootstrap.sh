#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap_script="$repo_root/ops/production/host-bootstrap.sh"
smoke_directory="$(mktemp -d)"

cleanup() {
    rm -rf -- "$smoke_directory"
}
trap cleanup EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

expect_failure() {
    local name="$1"
    shift
    if "$@" >"$smoke_directory/$name.out" 2>&1; then
        fail "Host-bootstrap case '$name' passed unexpectedly."
    fi
    printf "Host-bootstrap case '%s' failed as expected.\n" "$name"
}

bash -n "$bootstrap_script"

public_key_file="$smoke_directory/operator.pub"
printf '%s\n' \
    'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIG9wZXJhdG9yLXNtb2tlLWtleS1tYXRlcmlhbA host-bootstrap-smoke' \
    > "$public_key_file"

prepare_output="$smoke_directory/prepare.out"
bash "$bootstrap_script" \
    --phase prepare \
    --admin-ipv4-cidr 203.0.113.10/32 \
    --operator-user gsoadmin \
    --operator-public-key-file "$public_key_file" \
    --ssh-port 22 \
    > "$prepare_output"

for expected in \
    'PLAN: phase=prepare' \
    'install security updates, Docker Engine with Compose, and PowerShell' \
    'create a key-only sudo operator without Docker-group membership' \
    'PLAN_ONLY: no host changes were made'; do
    grep -Fq "$expected" "$prepare_output" ||
        fail "Prepare plan is missing '$expected'."
done
if grep -Fq 'AAAAC3' "$prepare_output"; then
    fail "Prepare plan exposed public-key material."
fi

finalize_output="$smoke_directory/finalize.out"
bash "$bootstrap_script" \
    --phase finalize \
    --admin-ipv4-cidr 203.0.113.10/32 \
    --operator-user gsoadmin \
    --ssh-port 22 \
    > "$finalize_output"

for expected in \
    'PLAN: phase=finalize' \
    'require sudo from a verified operator SSH session' \
    'disable root, password, and keyboard-interactive SSH authentication' \
    'PLAN_ONLY: no host changes were made'; do
    grep -Fq "$expected" "$finalize_output" ||
        fail "Finalize plan is missing '$expected'."
done

expect_failure invalid-cidr \
    bash "$bootstrap_script" \
        --phase finalize \
        --admin-ipv4-cidr 203.0.113.999/32

expect_failure root-operator \
    bash "$bootstrap_script" \
        --phase finalize \
        --admin-ipv4-cidr 203.0.113.10/32 \
        --operator-user root

multiline_key_file="$smoke_directory/multiline.pub"
printf '%s\n%s\n' \
    'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGZpcnN0 first' \
    'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHNlY29uZA second' \
    > "$multiline_key_file"
expect_failure multiple-keys \
    bash "$bootstrap_script" \
        --phase prepare \
        --admin-ipv4-cidr 203.0.113.10/32 \
        --operator-public-key-file "$multiline_key_file"

expect_failure non-root-apply \
    bash "$bootstrap_script" \
        --phase finalize \
        --admin-ipv4-cidr 203.0.113.10/32 \
        --apply

printf 'Host-bootstrap smoke passed.\n'

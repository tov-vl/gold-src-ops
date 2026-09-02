#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
readiness_script="$repo_root/ops/gameserver/soak-readiness.sh"
smoke_directory="$(mktemp -d)"
forbidden_evidence_file="$repo_root/.gameserver-soak-readiness.json"
started_at_utc="2026-09-01T00:00:00.000Z"
completed_at_utc="2026-09-02T00:00:00.000Z"
incomplete_at_utc="2026-09-01T12:00:00.000Z"
invocation_id="0123456789abcdef0123456789abcdef"
main_process_id=4242

cleanup() {
    rm -f -- "$forbidden_evidence_file"
    rm -rf -- "$smoke_directory"
}
trap cleanup EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

write_baseline() {
    local path="$1"

    jq -n \
        --arg startedAtUtc "$started_at_utc" \
        --arg expectedCompletionAtUtc "$completed_at_utc" \
        --arg invocationId "$invocation_id" \
        --argjson mainProcessId "$main_process_id" \
        '{
            SchemaVersion: 1,
            Action: "V23SoakBaseline",
            StartedAtUtc: $startedAtUtc,
            ExpectedCompletionAtUtc: $expectedCompletionAtUtc,
            RequiredDurationHours: 24,
            GameServer: {
                ServiceState: "active",
                BootEnablement: "disabled",
                RestartCount: 0,
                InvocationId: $invocationId,
                MainProcessId: $mainProcessId,
                UdpListenerCount: 1
            }
        }' > "$path"
    chmod 0600 -- "$path"
}

write_snapshot() {
    local path="$1"
    local collected_at_utc="$2"
    local restart_count="${3:-0}"
    local observed_invocation_id="${4:-$invocation_id}"
    local observed_main_process_id="${5:-$main_process_id}"
    local listener_count="${6:-1}"
    local listener_owned="${7:-true}"

    jq -n \
        --arg collectedAtUtc "$collected_at_utc" \
        --arg invocationId "$observed_invocation_id" \
        --argjson restartCount "$restart_count" \
        --argjson mainProcessId "$observed_main_process_id" \
        --argjson udpListenerCount "$listener_count" \
        --argjson udpListenerOwnedByService "$listener_owned" \
        '{
            SchemaVersion: 1,
            CollectedAtUtc: $collectedAtUtc,
            GameServer: {
                ServiceState: "active",
                BootEnablement: "disabled",
                RestartCount: $restartCount,
                InvocationId: $invocationId,
                MainProcessId: $mainProcessId,
                UdpListenerCount: $udpListenerCount,
                UdpListenerOwnedByService: $udpListenerOwnedByService
            }
        }' > "$path"
    chmod 0600 -- "$path"
}

update_snapshot() {
    local path="$1"
    local filter="$2"
    local temporary_path="$path.tmp"

    jq "$filter" "$path" > "$temporary_path"
    chmod 0600 -- "$temporary_path"
    mv -f -- "$temporary_path" "$path"
}

invoke_case() {
    local name="$1"
    local snapshot_file="$2"
    local should_pass="$3"
    local expected_result="$4"
    local allow_incomplete="${5:-false}"
    local safe_name baseline_file evidence_file output_file exit_code
    local -a arguments

    safe_name="${name// /-}"
    baseline_file="$smoke_directory/$safe_name-baseline.json"
    evidence_file="$smoke_directory/$safe_name-evidence.json"
    output_file="$smoke_directory/$safe_name.out"
    write_baseline "$baseline_file"

    arguments=(
        "$readiness_script"
        --baseline-file "$baseline_file"
        --snapshot-file "$snapshot_file"
        --evidence-file "$evidence_file"
    )
    if [[ "$allow_incomplete" == "true" ]]; then
        arguments+=(--allow-incomplete)
    fi

    set +e
    bash "${arguments[@]}" > "$output_file" 2>&1
    exit_code=$?
    set -e

    if [[ "$should_pass" == "true" && "$exit_code" -ne 0 ]]; then
        cat "$output_file" >&2
        fail "Game-host soak case '$name' failed unexpectedly."
    fi
    if [[ "$should_pass" == "false" && "$exit_code" -eq 0 ]]; then
        fail "Game-host soak case '$name' passed unexpectedly."
    fi
    [[ -f "$evidence_file" ]] ||
        fail "Game-host soak case '$name' did not write evidence."
    [[ "$(stat -c '%a' -- "$evidence_file")" == "600" ]] ||
        fail "Game-host soak case '$name' wrote non-owner-only evidence."

    jq -e \
        --arg expectedResult "$expected_result" \
        '.SchemaVersion == 1 and
         .Action == "V23GameHostSoakReadiness" and
         .Source == "Snapshot" and
         .TargetEvidence == false and
         .Result == $expectedResult' \
        "$evidence_file" >/dev/null ||
        fail "Game-host soak case '$name' wrote an unexpected evidence contract."

    if grep -Eiq \
        'password|authorization|203\.0\.113\.|27015|4242|0123456789abcdef' \
        "$evidence_file"; then
        fail "Game-host soak case '$name' exposed sensitive or raw runtime data."
    fi

    printf "Game-host soak case '%s' behaved as expected.\n" "$name"
}

assert_repository_evidence_rejected() {
    local baseline_file="$smoke_directory/repository-baseline.json"
    local snapshot_file="$smoke_directory/repository-snapshot.json"
    local output_file="$smoke_directory/repository-evidence.out"

    write_baseline "$baseline_file"
    write_snapshot "$snapshot_file" "$completed_at_utc"
    if bash "$readiness_script" \
        --baseline-file "$baseline_file" \
        --snapshot-file "$snapshot_file" \
        --evidence-file "$forbidden_evidence_file" \
        > "$output_file" 2>&1; then
        fail "Repository-local game-host soak evidence was accepted unexpectedly."
    fi
    [[ ! -e "$forbidden_evidence_file" ]] ||
        fail "Repository-local game-host soak evidence was written unexpectedly."
    grep -Fq 'outside the repository' "$output_file" ||
        fail "Repository-local evidence rejection did not explain the boundary."

    printf '%s\n' "Repository-local game-host soak evidence was rejected as expected."
}

bash -n "$readiness_script"

completed_snapshot="$smoke_directory/completed.json"
write_snapshot "$completed_snapshot" "$completed_at_utc"
invoke_case "completed" "$completed_snapshot" true Passed

incomplete_snapshot="$smoke_directory/incomplete.json"
write_snapshot "$incomplete_snapshot" "$incomplete_at_utc"
invoke_case "in-progress-allowed" "$incomplete_snapshot" true InProgress true
invoke_case "in-progress-rejected" "$incomplete_snapshot" false Failed

restart_snapshot="$smoke_directory/restart-drift.json"
write_snapshot "$restart_snapshot" "$completed_at_utc" 1
invoke_case "restart-drift" "$restart_snapshot" false Failed

service_snapshot="$smoke_directory/service-drift.json"
write_snapshot "$service_snapshot" "$completed_at_utc"
update_snapshot "$service_snapshot" '.GameServer.ServiceState = "inactive"'
invoke_case "service-drift" "$service_snapshot" false Failed

boot_snapshot="$smoke_directory/boot-drift.json"
write_snapshot "$boot_snapshot" "$completed_at_utc"
update_snapshot "$boot_snapshot" '.GameServer.BootEnablement = "enabled"'
invoke_case "boot-drift" "$boot_snapshot" false Failed

invocation_snapshot="$smoke_directory/invocation-drift.json"
write_snapshot "$invocation_snapshot" "$completed_at_utc" 0 \
    fedcba9876543210fedcba9876543210
invoke_case "invocation-drift" "$invocation_snapshot" false Failed

process_snapshot="$smoke_directory/process-drift.json"
write_snapshot "$process_snapshot" "$completed_at_utc" 0 "$invocation_id" 4243
invoke_case "process-drift" "$process_snapshot" false Failed

listener_count_snapshot="$smoke_directory/listener-count-drift.json"
write_snapshot "$listener_count_snapshot" "$completed_at_utc" 0 \
    "$invocation_id" "$main_process_id" 2
invoke_case "listener-count-drift" "$listener_count_snapshot" false Failed

listener_owner_snapshot="$smoke_directory/listener-owner-drift.json"
write_snapshot "$listener_owner_snapshot" "$completed_at_utc" 0 \
    "$invocation_id" "$main_process_id" 1 false
invoke_case "listener-owner-drift" "$listener_owner_snapshot" false Failed

assert_repository_evidence_rejected

printf '%s\n' "Game-host soak-readiness smoke passed."

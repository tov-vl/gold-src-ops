#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly SERVICE_NAME="goldsrcops-gameserver.service"
readonly PREPARED_MARKER="/etc/goldsrcops/gameserver/host-prepared"

baseline_file=""
snapshot_file=""
evidence_file=""
expected_duration_hours=24
allow_incomplete=false
source_kind="Live"
target_evidence=true
checks='[]'
failed_check_count=0

usage() {
    cat <<'EOF'
Usage:
  soak-readiness.sh --baseline-file PATH [--evidence-file PATH]
                    [--expected-duration-hours HOURS] [--allow-incomplete]

  soak-readiness.sh --baseline-file PATH --snapshot-file PATH
                    [--evidence-file PATH]
                    [--expected-duration-hours HOURS] [--allow-incomplete]

Live mode must run as root on the game-server host. It compares the active
systemd invocation, main process, restart count, boot enablement, and UDP
listener with the owner-only release-soak baseline. It does not start, stop,
restart, enable, or reconfigure the service.

Snapshot mode evaluates deterministic non-target input for CI. Evidence is
sanitized, written atomically outside the repository, and contains no endpoint,
port, process identifier, invocation identifier, credential, or raw command
output.
EOF
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' is unavailable."
}

validate_positive_integer() {
    local description="$1"
    local value="$2"

    [[ "$value" =~ ^[0-9]+$ ]] || fail "$description must be a positive integer."
    ((10#$value >= 1)) || fail "$description must be a positive integer."
}

resolve_path() {
    realpath -m -- "$1"
}

is_inside_repository() {
    local path
    path="$(resolve_path "$1")"
    [[ "$path" == "$repo_root" || "$path" == "$repo_root/"* ]]
}

assert_regular_file() {
    local path="$1"
    local description="$2"

    [[ -f "$path" && ! -L "$path" ]] ||
        fail "$description must be a regular non-symbolic-link file."
}

assert_owner_only_file() {
    local path="$1"
    local description="$2"
    local require_root_owner="$3"
    local mode owner

    assert_regular_file "$path" "$description"
    mode="$(stat -c '%a' -- "$path")"
    [[ "$mode" == "600" ]] || fail "$description must have mode 0600."

    if [[ "$require_root_owner" == "true" ]]; then
        owner="$(stat -c '%u:%g' -- "$path")"
        [[ "$owner" == "0:0" ]] || fail "$description must be owned by root:root."
    fi
}

assert_root_group_readable_file() {
    local path="$1"
    local description="$2"
    local mode owner

    assert_regular_file "$path" "$description"
    mode="$(stat -c '%a' -- "$path")"
    owner="$(stat -c '%u' -- "$path")"
    [[ "$mode" == "640" ]] || fail "$description must have mode 0640."
    [[ "$owner" == "0" ]] || fail "$description must be owned by root."
}

assert_json_file() {
    local path="$1"
    local description="$2"

    assert_regular_file "$path" "$description"
    jq -e '.' "$path" >/dev/null 2>&1 || fail "$description is not valid JSON."
}

parse_timestamp_nanoseconds() {
    local value="$1"
    local description="$2"
    local parsed

    if ! parsed="$(date -u -d "$value" '+%s%N' 2>/dev/null)"; then
        fail "$description must be an ISO 8601 timestamp with an offset."
    fi
    [[ "$parsed" =~ ^[0-9]{10,19}$ ]] ||
        fail "$description could not be converted to a UTC instant."
    printf '%s\n' "$parsed"
}

add_check() {
    local name="$1"
    local passed="$2"
    local detail="$3"

    checks="$(jq -cn \
        --argjson checks "$checks" \
        --arg name "$name" \
        --argjson passed "$passed" \
        --arg detail "$detail" \
        '$checks + [{Name: $name, Passed: $passed, Detail: $detail}]')"
    if [[ "$passed" != "true" ]]; then
        ((failed_check_count += 1))
    fi
}

read_prepared_game_port() {
    local key value
    local schema_version=""
    local operator_user=""
    local service_user=""
    local ssh_port=""
    local game_port=""
    local marker_group service_group
    declare -A seen=()

    assert_root_group_readable_file "$PREPARED_MARKER" \
        "The game-host readiness marker"

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-host readiness marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker contains a duplicate key."
        seen[$key]=1

        case "$key" in
            schema_version)
                schema_version="$value"
                ;;
            game_port)
                game_port="$value"
                ;;
            operator_user)
                operator_user="$value"
                ;;
            service_user)
                service_user="$value"
                ;;
            ssh_port)
                ssh_port="$value"
                ;;
            *)
                fail "The game-host readiness marker contains an unknown key."
                ;;
        esac
    done < "$PREPARED_MARKER"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 5 ]] ||
        fail "The game-host readiness marker contract is invalid."
    [[ "$operator_user" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$operator_user" != "root" ]] ||
        fail "The prepared operator user is invalid."
    [[ "$service_user" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$service_user" != "root" ]] ||
        fail "The prepared service user is invalid."
    validate_positive_integer "The prepared SSH port" "$ssh_port"
    ((10#$ssh_port <= 65535)) || fail "The prepared SSH port must not exceed 65535."
    validate_positive_integer "The prepared game port" "$game_port"
    ((10#$game_port <= 65535)) || fail "The prepared game port must not exceed 65535."

    marker_group="$(stat -c '%g' -- "$PREPARED_MARKER")"
    if ! service_group="$(id -g "$service_user" 2>/dev/null)"; then
        fail "The prepared service user does not exist."
    fi
    [[ "$marker_group" == "$service_group" ]] ||
        fail "The game-host readiness marker group does not match the service user."
    printf '%s\n' "$game_port"
}

read_systemd_property() {
    local property="$1"
    local value

    if ! value="$(systemctl show "$SERVICE_NAME" \
        --property="$property" --value 2>/dev/null)"; then
        fail "Could not inspect the game-server service."
    fi
    [[ -n "$value" ]] || fail "The game-server service property is empty."
    printf '%s\n' "$value"
}

collect_live_observation() {
    local game_port service_state boot_enablement restart_count invocation_id
    local main_process_id control_group listener_output listener_count
    local listener_owned remaining listener_pid collected_at_utc
    declare -A listener_pids=()

    [[ "$(id -u)" == "0" ]] || fail "Live evaluation must run as root."
    assert_owner_only_file "$baseline_file" "The live soak baseline" true

    game_port="$(read_prepared_game_port)"
    service_state="$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)"
    boot_enablement="$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)"
    restart_count="$(read_systemd_property NRestarts)"
    invocation_id="$(read_systemd_property InvocationID)"
    main_process_id="$(read_systemd_property MainPID)"
    control_group="$(read_systemd_property ControlGroup)"

    [[ "$restart_count" =~ ^[0-9]+$ ]] || fail "The service restart count is invalid."
    [[ "$invocation_id" =~ ^[0-9A-Fa-f]{32}$ ]] ||
        fail "The service invocation identifier is invalid."
    validate_positive_integer "The service main process identifier" "$main_process_id"
    [[ "$control_group" == /* ]] || fail "The service control group is invalid."

    if ! listener_output="$(ss -H -lunp "sport = :$game_port" 2>/dev/null)"; then
        fail "Could not inspect the configured game-server UDP listener."
    fi
    if [[ -z "$listener_output" ]]; then
        listener_count=0
    else
        listener_count="$(awk 'NF { count++ } END { print count + 0 }' <<< "$listener_output")"
    fi

    remaining="$listener_output"
    while [[ "$remaining" =~ pid=([0-9]+) ]]; do
        listener_pid="${BASH_REMATCH[1]}"
        listener_pids[$listener_pid]=1
        remaining="${remaining#*pid="$listener_pid"}"
    done

    listener_owned=true
    if ((listener_count != 1 || ${#listener_pids[@]} == 0)); then
        listener_owned=false
    else
        for listener_pid in "${!listener_pids[@]}"; do
            if [[ ! -r "/proc/$listener_pid/cgroup" ]] ||
                ! grep -Fq -- "$control_group" "/proc/$listener_pid/cgroup"; then
                listener_owned=false
                break
            fi
        done
    fi

    collected_at_utc="$(date -u '+%Y-%m-%dT%H:%M:%S.%3NZ')"
    jq -cn \
        --arg collectedAtUtc "$collected_at_utc" \
        --arg serviceState "$service_state" \
        --arg bootEnablement "$boot_enablement" \
        --argjson restartCount "$restart_count" \
        --arg invocationId "$invocation_id" \
        --argjson mainProcessId "$main_process_id" \
        --argjson udpListenerCount "$listener_count" \
        --argjson udpListenerOwnedByService "$listener_owned" \
        '{
            SchemaVersion: 1,
            CollectedAtUtc: $collectedAtUtc,
            GameServer: {
                ServiceState: $serviceState,
                BootEnablement: $bootEnablement,
                RestartCount: $restartCount,
                InvocationId: $invocationId,
                MainProcessId: $mainProcessId,
                UdpListenerCount: $udpListenerCount,
                UdpListenerOwnedByService: $udpListenerOwnedByService
            }
        }'
}

write_evidence() {
    local content="$1"
    local resolved directory filename temporary_path

    [[ -n "$evidence_file" ]] || return 0
    [[ ! -L "$evidence_file" ]] || fail "The evidence path must not be a symbolic link."
    is_inside_repository "$evidence_file" &&
        fail "Game-host soak evidence must be written outside the repository."

    resolved="$(resolve_path "$evidence_file")"
    directory="$(dirname -- "$resolved")"
    filename="$(basename -- "$resolved")"
    mkdir -p -- "$directory"
    temporary_path="$(mktemp --tmpdir="$directory" ".$filename.XXXXXX")"
    if ! chmod 0600 -- "$temporary_path" ||
        ! printf '%s\n' "$content" > "$temporary_path" ||
        ! mv -f -- "$temporary_path" "$resolved"; then
        rm -f -- "$temporary_path"
        fail "Could not publish game-host soak evidence atomically."
    fi
}

while (($# > 0)); do
    case "$1" in
        --baseline-file)
            (($# >= 2)) || fail "--baseline-file requires a value."
            baseline_file="$2"
            shift 2
            ;;
        --snapshot-file)
            (($# >= 2)) || fail "--snapshot-file requires a value."
            snapshot_file="$2"
            shift 2
            ;;
        --evidence-file)
            (($# >= 2)) || fail "--evidence-file requires a value."
            evidence_file="$2"
            shift 2
            ;;
        --expected-duration-hours)
            (($# >= 2)) || fail "--expected-duration-hours requires a value."
            expected_duration_hours="$2"
            shift 2
            ;;
        --allow-incomplete)
            allow_incomplete=true
            shift
            ;;
        --help | -h)
            usage
            exit 0
            ;;
        *)
            fail "Unknown option '$1'."
            ;;
    esac
done

require_command awk
require_command basename
require_command date
require_command dirname
require_command jq
require_command mktemp
require_command realpath
require_command stat

[[ -n "$baseline_file" ]] || fail "--baseline-file is required."
validate_positive_integer "The expected duration" "$expected_duration_hours"
((10#$expected_duration_hours <= 168)) ||
    fail "The expected duration must not exceed 168 hours."

if [[ -n "$snapshot_file" ]]; then
    source_kind="Snapshot"
    target_evidence=false
fi

script_source="${BASH_SOURCE[0]:-}"
if [[ -n "$script_source" && "$script_source" != "-" ]]; then
    candidate_repo_root="$(cd "$(dirname "$script_source")/../.." && pwd -P)"
    if [[ -f "$candidate_repo_root/GoldSrcOps.sln" &&
        -d "$candidate_repo_root/ops/gameserver" ]]; then
        repo_root="$candidate_repo_root"
    else
        repo_root="${GOLDSRCOPS_REPO_ROOT:-/opt/goldsrcops}"
    fi
else
    repo_root="${GOLDSRCOPS_REPO_ROOT:-/opt/goldsrcops}"
fi
repo_root="$(resolve_path "$repo_root")"

assert_json_file "$baseline_file" "The soak baseline"
if [[ "$source_kind" == "Live" ]] && is_inside_repository "$baseline_file"; then
    fail "The live soak baseline must remain outside the repository."
fi

if ! jq -e '
    .SchemaVersion == 1 and
    .Action == "V23SoakBaseline" and
    ((.StartedAtUtc | type) == "string") and
    ((.ExpectedCompletionAtUtc | type) == "string") and
    ((.RequiredDurationHours | type) == "number") and
    (.RequiredDurationHours == (.RequiredDurationHours | floor)) and
    ((.GameServer | type) == "object") and
    (.GameServer.ServiceState == "active") and
    (.GameServer.BootEnablement == "disabled") and
    ((.GameServer.RestartCount | type) == "number") and
    (.GameServer.RestartCount >= 0) and
    (.GameServer.RestartCount == (.GameServer.RestartCount | floor)) and
    ((.GameServer.InvocationId | type) == "string") and
    (.GameServer.InvocationId | test("^[0-9A-Fa-f]{32}$")) and
    ((.GameServer.MainProcessId | type) == "number") and
    (.GameServer.MainProcessId >= 1) and
    (.GameServer.MainProcessId == (.GameServer.MainProcessId | floor)) and
    (.GameServer.UdpListenerCount == 1)
' "$baseline_file" >/dev/null; then
    fail "The soak baseline contract is invalid."
fi

baseline_duration="$(jq -er '.RequiredDurationHours' "$baseline_file")"
[[ "$baseline_duration" == "$expected_duration_hours" ]] ||
    fail "The soak baseline duration does not match the expected release gate."
started_at_utc="$(jq -er '.StartedAtUtc' "$baseline_file")"
expected_completion_at_utc="$(jq -er '.ExpectedCompletionAtUtc' "$baseline_file")"
started_at_ns="$(parse_timestamp_nanoseconds "$started_at_utc" "The soak start time")"
expected_completion_at_ns="$(parse_timestamp_nanoseconds \
    "$expected_completion_at_utc" "The soak completion time")"
required_duration_ns=$((10#$expected_duration_hours * 3600 * 1000000000))
((10#$expected_completion_at_ns - 10#$started_at_ns == required_duration_ns)) ||
    fail "The soak completion time does not match its duration."

if [[ "$source_kind" == "Snapshot" ]]; then
    assert_owner_only_file "$baseline_file" "The snapshot soak baseline" false
    assert_json_file "$snapshot_file" "The soak observation snapshot"
    assert_owner_only_file "$snapshot_file" "The soak observation snapshot" false
    observation="$(jq -c '.' "$snapshot_file")"
else
    require_command grep
    require_command id
    require_command ss
    require_command systemctl
    observation="$(collect_live_observation)"
fi

if ! jq -e '
    .SchemaVersion == 1 and
    ((.CollectedAtUtc | type) == "string") and
    ((.GameServer | type) == "object") and
    ((.GameServer.ServiceState | type) == "string") and
    ((.GameServer.BootEnablement | type) == "string") and
    ((.GameServer.RestartCount | type) == "number") and
    (.GameServer.RestartCount >= 0) and
    (.GameServer.RestartCount == (.GameServer.RestartCount | floor)) and
    ((.GameServer.InvocationId | type) == "string") and
    ((.GameServer.MainProcessId | type) == "number") and
    (.GameServer.MainProcessId >= 1) and
    (.GameServer.MainProcessId == (.GameServer.MainProcessId | floor)) and
    ((.GameServer.UdpListenerCount | type) == "number") and
    (.GameServer.UdpListenerCount >= 0) and
    (.GameServer.UdpListenerCount == (.GameServer.UdpListenerCount | floor)) and
    ((.GameServer.UdpListenerOwnedByService | type) == "boolean")
' <<< "$observation" >/dev/null; then
    fail "The soak observation contract is invalid."
fi

collected_at_utc="$(jq -er '.CollectedAtUtc' <<< "$observation")"
collected_at_ns="$(parse_timestamp_nanoseconds \
    "$collected_at_utc" "The soak observation time")"
((10#$collected_at_ns >= 10#$started_at_ns)) ||
    fail "The soak observation predates its baseline."

duration_complete=false
if ((10#$collected_at_ns >= 10#$expected_completion_at_ns)); then
    duration_complete=true
    add_check "Soak duration" true \
        "The required game-host release-soak interval is complete."
elif [[ "$allow_incomplete" == "true" ]]; then
    add_check "Soak duration" true \
        "The game-host release soak is still in progress."
else
    add_check "Soak duration" false \
        "The required game-host release-soak interval is incomplete."
fi

baseline_service_state="$(jq -er '.GameServer.ServiceState' "$baseline_file")"
observed_service_state="$(jq -er '.GameServer.ServiceState' <<< "$observation")"
service_active=false
if [[ "$baseline_service_state" == "active" &&
    "$observed_service_state" == "$baseline_service_state" ]]; then
    service_active=true
fi
add_check "Service state" "$service_active" \
    "$([[ "$service_active" == "true" ]] && printf '%s' \
        'The game-server service remains active.' || printf '%s' \
        'The game-server service is not active as recorded in the baseline.')"

baseline_boot_enablement="$(jq -er '.GameServer.BootEnablement' "$baseline_file")"
observed_boot_enablement="$(jq -er '.GameServer.BootEnablement' <<< "$observation")"
boot_disabled=false
if [[ "$baseline_boot_enablement" == "disabled" &&
    "$observed_boot_enablement" == "$baseline_boot_enablement" ]]; then
    boot_disabled=true
fi
add_check "Boot enablement" "$boot_disabled" \
    "$([[ "$boot_disabled" == "true" ]] && printf '%s' \
        'The game-server service remains disabled across boot.' || printf '%s' \
        'The game-server boot enablement drifted from the baseline.')"

baseline_restart_count="$(jq -er '.GameServer.RestartCount' "$baseline_file")"
observed_restart_count="$(jq -er '.GameServer.RestartCount' <<< "$observation")"
restart_count_unchanged=false
if [[ "$observed_restart_count" == "$baseline_restart_count" ]]; then
    restart_count_unchanged=true
fi
add_check "Restart count" "$restart_count_unchanged" \
    "$([[ "$restart_count_unchanged" == "true" ]] && printf '%s' \
        'The game-server restart count is unchanged.' || printf '%s' \
        'The game-server restart count changed after the baseline.')"

baseline_invocation_id="$(jq -er '.GameServer.InvocationId' "$baseline_file")"
observed_invocation_id="$(jq -er '.GameServer.InvocationId' <<< "$observation")"
invocation_unchanged=false
if [[ "$observed_invocation_id" == "$baseline_invocation_id" ]]; then
    invocation_unchanged=true
fi
add_check "Service invocation" "$invocation_unchanged" \
    "$([[ "$invocation_unchanged" == "true" ]] && printf '%s' \
        'The systemd service invocation is unchanged.' || printf '%s' \
        'The systemd service invocation changed after the baseline.')"

baseline_main_process_id="$(jq -er '.GameServer.MainProcessId' "$baseline_file")"
observed_main_process_id="$(jq -er '.GameServer.MainProcessId' <<< "$observation")"
main_process_unchanged=false
if [[ "$observed_main_process_id" == "$baseline_main_process_id" ]]; then
    main_process_unchanged=true
fi
add_check "Main process" "$main_process_unchanged" \
    "$([[ "$main_process_unchanged" == "true" ]] && printf '%s' \
        'The game-server main process is unchanged.' || printf '%s' \
        'The game-server main process changed after the baseline.')"

baseline_listener_count="$(jq -er '.GameServer.UdpListenerCount' "$baseline_file")"
observed_listener_count="$(jq -er '.GameServer.UdpListenerCount' <<< "$observation")"
listener_count_matches=false
if [[ "$baseline_listener_count" == "1" &&
    "$observed_listener_count" == "$baseline_listener_count" ]]; then
    listener_count_matches=true
fi
add_check "UDP listener count" "$listener_count_matches" \
    "$([[ "$listener_count_matches" == "true" ]] && printf '%s' \
        'Exactly one configured game-server UDP listener remains present.' || printf '%s' \
        'The configured game-server UDP listener count drifted from the baseline.')"

listener_owned="$(jq -r '.GameServer.UdpListenerOwnedByService' <<< "$observation")"
add_check "UDP listener ownership" "$listener_owned" \
    "$([[ "$listener_owned" == "true" ]] && printf '%s' \
        'The UDP listener belongs to the game-server service control group.' || printf '%s' \
        'The UDP listener could not be attributed to the game-server service.')"

if ((failed_check_count > 0)); then
    result="Failed"
elif [[ "$duration_complete" == "true" ]]; then
    result="Passed"
else
    result="InProgress"
fi

evidence="$(jq -cn \
    --arg source "$source_kind" \
    --argjson targetEvidence "$target_evidence" \
    --arg result "$result" \
    --arg collectedAtUtc "$collected_at_utc" \
    --arg startedAtUtc "$started_at_utc" \
    --arg expectedCompletionAtUtc "$expected_completion_at_utc" \
    --argjson requiredDurationHours "$expected_duration_hours" \
    --argjson serviceActive "$service_active" \
    --argjson bootDisabled "$boot_disabled" \
    --argjson restartCountUnchanged "$restart_count_unchanged" \
    --argjson invocationUnchanged "$invocation_unchanged" \
    --argjson mainProcessUnchanged "$main_process_unchanged" \
    --argjson udpListenerCountMatches "$listener_count_matches" \
    --argjson udpListenerOwnedByService "$listener_owned" \
    --argjson checks "$checks" \
    '{
        SchemaVersion: 1,
        Action: "V23GameHostSoakReadiness",
        Source: $source,
        TargetEvidence: $targetEvidence,
        Result: $result,
        CollectedAtUtc: $collectedAtUtc,
        StartedAtUtc: $startedAtUtc,
        ExpectedCompletionAtUtc: $expectedCompletionAtUtc,
        RequiredDurationHours: $requiredDurationHours,
        Continuity: {
            ServiceActive: $serviceActive,
            BootDisabled: $bootDisabled,
            RestartCountUnchanged: $restartCountUnchanged,
            InvocationUnchanged: $invocationUnchanged,
            MainProcessUnchanged: $mainProcessUnchanged,
            UdpListenerCountMatches: $udpListenerCountMatches,
            UdpListenerOwnedByService: $udpListenerOwnedByService
        },
        Limitations: [
            "This evidence verifies one terminal host observation, not continuous external A2S availability.",
            "Endpoint address stability is checked separately from the control-plane side.",
            "Player and bot outcomes are evaluated from persisted control-plane polling evidence."
        ],
        Checks: $checks
    }')"

write_evidence "$evidence"

while IFS= read -r check; do
    check_passed="$(jq -r '.Passed' <<< "$check")"
    check_marker="FAIL"
    [[ "$check_passed" == "true" ]] && check_marker="PASS"
    printf '[%s] %s: %s\n' \
        "$check_marker" \
        "$(jq -r '.Name' <<< "$check")" \
        "$(jq -r '.Detail' <<< "$check")"
done < <(jq -c '.[]' <<< "$checks")

if [[ "$result" == "Failed" ]]; then
    fail "Game-host soak readiness failed."
elif [[ "$result" == "InProgress" ]]; then
    printf '%s\n' \
        "Game-host soak readiness checks passed; the required duration is still in progress."
else
    printf '%s\n' "Game-host soak readiness passed."
fi

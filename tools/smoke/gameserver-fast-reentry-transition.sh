#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/fast-reentry-transition.sh"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

assert_order() {
    local output="$1" expected current previous=0
    shift
    for expected in "$@"; do
        current="$(grep -nFx "$expected" "$output" | awk -F: -v after="$previous" '$1 > after { print $1; exit }')"
        [[ -n "$current" ]] || fail "The transition order is invalid at '$expected'."
        previous="$current"
    done
}

make_fixture() {
    local config="$1/config" game="$1/service/server/cstrike"
    local managed="$1/backups/managed-profile-20260924T000000Z-abcdef"
    local hash
    mkdir -p "$config" "$game" "$managed" "$1/backups/persistent-gameplay" \
        "$1/service/game-event-agent/queue" "$game/addons/amxmodx/data/goldsrcops-spool"
    chmod 0700 "$1/backups" "$managed"
    printf 'classic public baseline\n' > "$managed/server-public.cfg"
    printf 'classic runtime baseline\n' > "$managed/runtime-enabled"
    chmod 0600 "$managed/server-public.cfg" "$managed/runtime-enabled"
    printf 'exec goldsrcops-private.cfg\nexec goldsrcops-managed-profile.cfg\necho "GoldSrcOps public runtime configuration loaded"\n' > "$config/server-public.cfg"
    printf 'de_dust2\nde_inferno\nde_nuke\nde_train\ncs_office\n' > "$game/goldsrcops-mapcycle.txt"
    printf 'hostname "GoldSrcOps Public Classic"\nmapcyclefile "goldsrcops-mapcycle.txt"\nmapchangecfgfile "goldsrcops-managed-profile.cfg"\n' > "$game/goldsrcops-managed-profile.cfg"
    hash="$(sha256sum "$config/server-public.cfg" | cut -d' ' -f1)"
    cat > "$config/runtime-enabled" <<EOF
schema_version=1
runtime_marker_sha256=$hash
service_unit_sha256=$hash
public_config_sha256=$hash
rcon_source_policy=ssh-ufw-exact-ipv4-32
rcon_secret_transport=stdin
service_autostart=disabled
EOF
    cat > "$config/managed-profile-active" <<EOF
schema_version=1
profile_id=public-classic-v1
backup_name=managed-profile-20260924T000000Z-abcdef
baseline_public_sha256=$(sha256sum "$managed/server-public.cfg" | cut -d' ' -f1)
baseline_runtime_enabled_sha256=$(sha256sum "$managed/runtime-enabled" | cut -d' ' -f1)
public_sha256=$(sha256sum "$config/server-public.cfg" | cut -d' ' -f1)
runtime_enabled_sha256=$(sha256sum "$config/runtime-enabled" | cut -d' ' -f1)
profile_sha256=$(sha256sum "$game/goldsrcops-managed-profile.cfg" | cut -d' ' -f1)
mapcycle_sha256=$(sha256sum "$game/goldsrcops-mapcycle.txt" | cut -d' ' -f1)
EOF
    printf '#!/usr/bin/env bash\nexit 0\n' > "$1/persistent-guard"
    chmod 0755 "$1/persistent-guard"
    hash="$(printf 'unchanged-boundary' | sha256sum | cut -d' ' -f1)"
    cat > "$config/game-event-persistent-active" <<EOF
schema_version=1
policy_id=persistent-gameplay-v1
backup_name=persistent-gameplay-20260924T000000Z-abcdef
guard_sha256=$(sha256sum "$1/persistent-guard" | cut -d' ' -f1)
game_drop_in_sha256=$hash
agent_drop_in_sha256=$hash
pilot_marker_sha256=$hash
pilot_gate_sha256=$hash
activation_state_sha256=$hash
manifest_sha256=$hash
liblist_sha256=$hash
environment_sha256=$hash
producer_sha256=$hash
game_unit_sha256=$hash
agent_unit_sha256=$hash
EOF
    chmod 0640 "$config/server-public.cfg" "$config/runtime-enabled" \
        "$config/managed-profile-active" "$config/game-event-persistent-active" \
        "$game/goldsrcops-managed-profile.cfg" "$game/goldsrcops-mapcycle.txt"
    printf 'queue-evidence\n' > "$1/service/game-event-agent/queue/receipt"
    printf 'spool-evidence\n' > "$game/addons/amxmodx/data/goldsrcops-spool/receipt"
    printf 'active\n' > "$1/game.active"
    printf 'enabled\n' > "$1/game.enabled"
    printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n' > "$1/game.invocation"
    printf 'active\n' > "$1/agent.active"
    printf 'enabled\n' > "$1/agent.enabled"
    printf 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n' > "$1/agent.invocation"
}

if [[ "${1:-}" == --case ]]; then
    case_root="$2"
    case_action="$3"
    export GOLDSRCOPS_CONFIGURATION_DIRECTORY="$case_root/config"
    export GOLDSRCOPS_SERVICE_HOME="$case_root/service"
    export GOLDSRCOPS_BACKUP_ROOT="$case_root/backups/persistent-gameplay"
    # shellcheck source=ops/gameserver/fast-reentry-transition.sh
    source "$workflow"
    service_group=root
    service_user=root
    installed_persistent_guard="$case_root/persistent-guard"
    mock_log="$case_root/operations.log"

    require_apply_environment() { :; }
    switch_verify_source() {
        verify_sha256 "$FAST_PROFILE_SOURCE" "$FAST_PROFILE_SHA256"
        if [[ "$switch_operation" == upgrade-loadout ]]; then
            verify_sha256 "$FAST_LOADOUT_PROFILE_SOURCE" "$FAST_LOADOUT_PROFILE_SHA256"
        fi
    }
    verify_baseline_record() { :; }
    verify_persistent_files() {
        read_persistent_marker
        verify_sha256 "$installed_persistent_guard" "$marker_guard_sha256"
        if [[ "$marker_schema_version" == 2 ]]; then
            verify_fast_profile_files
        fi
    }
    require_settled_agent_status() { [[ -f "$case_root/service/game-event-agent/queue/receipt" ]]; }
    # shellcheck disable=SC2329
    capture_agent_status() { printf '{"schemaVersion":1,"queue":{"pending":0}}\n'; }
    # shellcheck disable=SC2329
    runuser() { [[ "$1" == -u && "$2" == root && "$3" == -- ]]; }
    mock_install_failed=false
    install() {
        local destination="${*: -1}"
        if [[ "$case_action" == reject-install && "$mock_install_failed" == false &&
            "$destination" == "$active_profile_marker" ]]; then
            mock_install_failed=true
            return 42
        fi
        if [[ "$case_action" == upgrade-install && "$mock_install_failed" == false &&
            "$switch_operation" == upgrade-loadout && "$destination" == "$active_profile_marker" ]]; then
            mock_install_failed=true
            return 42
        fi
        command install "$@"
    }
    systemctl() {
        local action="$1" service="$2" unit
        case "$service" in
            goldsrcops-gameserver.service) unit=game ;;
            goldsrcops-game-event-agent.service) unit=agent ;;
            *) return 1 ;;
        esac
        case "$action" in
            is-active) cat "$case_root/$unit.active" ;;
            is-enabled) cat "$case_root/$unit.enabled" ;;
            show)
                case "$4" in
                    InvocationID) cat "$case_root/$unit.invocation" ;;
                    NRestarts) printf '0\n' ;;
                    *) return 1 ;;
                esac
                ;;
            disable|enable|stop|start)
                printf '%s %s\n' "$action" "$unit" >> "$mock_log"
                case "$action" in
                    disable) printf 'disabled\n' > "$case_root/$unit.enabled" ;;
                    enable) printf 'enabled\n' > "$case_root/$unit.enabled" ;;
                    stop) printf 'inactive\n' > "$case_root/$unit.active" ;;
                    start)
                        verify_persistent_files || return 1
                        printf 'active\n' > "$case_root/$unit.active"
                        if [[ "$unit" == game ]]; then
                            if [[ "$(cat "$case_root/$unit.invocation")" == aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ]]; then
                                printf 'cccccccccccccccccccccccccccccccc\n' > "$case_root/$unit.invocation"
                            elif [[ "$(cat "$case_root/$unit.invocation")" == cccccccccccccccccccccccccccccccc ]]; then
                                printf 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\n' > "$case_root/$unit.invocation"
                            else
                                printf '11111111111111111111111111111111\n' > "$case_root/$unit.invocation"
                            fi
                        else
                            if [[ "$(cat "$case_root/$unit.invocation")" == bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb ]]; then
                                printf 'dddddddddddddddddddddddddddddddd\n' > "$case_root/$unit.invocation"
                            elif [[ "$(cat "$case_root/$unit.invocation")" == dddddddddddddddddddddddddddddddd ]]; then
                                printf 'ffffffffffffffffffffffffffffffff\n' > "$case_root/$unit.invocation"
                            else
                                printf '22222222222222222222222222222222\n' > "$case_root/$unit.invocation"
                            fi
                        fi
                        ;;
                esac
                ;;
            *) return 1 ;;
        esac
    }
    switch_gate() {
        printf 'gate %s\n' "$1" >> "$mock_log"
        if [[ "$case_action" == reject-postcheck && "$1" == postcheck ]]; then
            return 1
        fi
        if [[ "$case_action" == upgrade-postcheck && "$switch_operation" == upgrade-loadout &&
            "$1" == postcheck ]]; then
            return 1
        fi
        if [[ "$case_action" == interrupt-postcheck && "$1" == postcheck ]]; then
            kill -TERM "$BASHPID"
            sleep 1
        fi
        require_settled_agent_status
    }
    switch_check_started() {
        require_unit_state "$GAME_SERVICE_NAME" active disabled
        require_unit_state "$AGENT_SERVICE_NAME" active disabled
        [[ "$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)" != "$1" ]] || return 1
        [[ "$(systemctl show "$AGENT_SERVICE_NAME" -p InvocationID --value)" != "$2" ]] || return 1
        verify_persistent_files
        require_settled_agent_status
    }

    if [[ "$case_action" == recover ]]; then
        switch_operation=recover
        switch_apply_operation
        [[ ! -e "$SWITCH_MARKER" ]] || fail "Recovery retained the active switch marker."
        [[ "$(cat "$case_root/game.enabled")" == enabled &&
            "$(cat "$case_root/agent.enabled")" == enabled ]] ||
            fail "Recovery did not enable independent boot entries."
        exit 0
    fi
    switch_operation=activate
    switch_apply_operation
    if [[ "$case_action" == upgrade-* || "$case_action" == success ]]; then
        switch_operation=upgrade-loadout
        switch_apply_operation
        if [[ "$case_action" == success ]]; then
            [[ "$(marker_value "$persistent_marker" profile_sha256)" == \
                "$FAST_LOADOUT_PROFILE_SHA256" ]] || fail "The loadout hash was not installed."
            verify_sha256 "$fast_profile_file" "$FAST_LOADOUT_PROFILE_SHA256"
            [[ "$(cat "$case_root/game.enabled")" == enabled &&
                "$(cat "$case_root/agent.enabled")" == enabled ]] ||
                fail "The loadout upgrade did not restore independent boot."
        fi
    fi
    if [[ "$case_action" == backup-drift ]]; then
        printf 'drift\n' >> "$switch_backup_directory/server-public.cfg"
        switch_operation=restore
        switch_apply_operation
        fail "A drifted owner-only backup was accepted."
    fi
    [[ "$case_action" != success ]] || {
        [[ "$(marker_value "$persistent_marker" schema_version)" == 2 ]] || fail "Fast schema was not installed."
        [[ "$(cat "$case_root/game.enabled")" == enabled ]] || fail "Game boot was not enabled."
        [[ "$(cat "$case_root/agent.enabled")" == enabled ]] || fail "Agent boot was not enabled."
        switch_verify_backup
        switch_operation=restore
        switch_apply_operation
        [[ ! -e "$SWITCH_MARKER" && ! -e "$fast_profile_file" ]] || fail "Fast files survived restoration."
        [[ "$(marker_value "$persistent_marker" schema_version)" == 1 ]] || fail "Classic schema was not restored."
        [[ "$(cat "$case_root/game.enabled")" == enabled ]] || fail "Classic game boot was not enabled."
        [[ "$(cat "$case_root/agent.enabled")" == enabled ]] || fail "Classic agent boot was not enabled."
    }
    exit 0
fi

if [[ "${1:-}" == --gate-timeout ]]; then
    case_root="$2"
    export GOLDSRCOPS_CONFIGURATION_DIRECTORY="$case_root/config"
    export GOLDSRCOPS_SERVICE_HOME="$case_root/service"
    export GOLDSRCOPS_BACKUP_ROOT="$case_root/backups/persistent-gameplay"
    # shellcheck source=ops/gameserver/fast-reentry-transition.sh
    source "$workflow"
    switch_gate_timeout=1
    systemctl() {
        if [[ "$1" == show ]]; then
            printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'
        elif [[ "$1" == is-active ]]; then
            printf 'active\n'
        else
            printf 'enabled\n'
        fi
    }
    require_settled_agent_status() { :; }
    trap 'switch_failed $?' EXIT
    switch_gate precheck
    fail "The external precheck did not time out."
fi

if [[ "${1:-}" == --gate-receipt ]]; then
    case_root="$2"
    receipt_case="$3"
    export GOLDSRCOPS_CONFIGURATION_DIRECTORY="$case_root/config"
    export GOLDSRCOPS_SERVICE_HOME="$case_root/service"
    export GOLDSRCOPS_BACKUP_ROOT="$case_root/backups/persistent-gameplay"
    # shellcheck source=ops/gameserver/fast-reentry-transition.sh
    source "$workflow"
    switch_gate_timeout=2
    systemctl() {
        case "$1" in
            show) cat "$case_root/game.invocation" ;;
            is-active) printf 'active\n' ;;
            is-enabled) printf 'enabled\n' ;;
            *) return 1 ;;
        esac
    }
    require_settled_agent_status() { :; }
    log() {
        local challenge fifo
        printf '%s\n' "$1"
        if [[ "$1" == EXTERNAL_PRECHECK_GATE_READY:* ]]; then
            IFS=' ' read -r _ challenge fifo <<< "$1"
            if [[ "$receipt_case" == invalid ]]; then
                challenge=00000000000000000000000000000000
            elif [[ "$receipt_case" == restarted ]]; then
                printf 'cccccccccccccccccccccccccccccccc\n' > "$case_root/game.invocation"
            fi
            (printf '%s\n' "$challenge" > "$fifo") &
        fi
    }
    trap 'switch_failed $?' EXIT
    switch_gate precheck
    [[ "$receipt_case" == valid ]] || fail "A bad external receipt passed."
    trap - EXIT
    exit 0
fi

"$BASH" -n "$workflow"
"$BASH" "$workflow" --activate > "$fixture_root/plan.out"
"$BASH" "$workflow" --upgrade-loadout > "$fixture_root/loadout-plan.out"
"$BASH" "$workflow" --restore > "$fixture_root/restore-plan.out"
"$BASH" "$workflow" --recover > "$fixture_root/recover-plan.out"
grep -Fq 'PLAN_ONLY: no host state or endpoint was inspected or changed' "$fixture_root/plan.out" ||
    fail "The activation plan is missing its read-only boundary."
grep -Fq 'install only the reviewed loadout revision' "$fixture_root/loadout-plan.out" ||
    fail "The loadout upgrade plan is missing."
grep -Fq 'restore the exact classic profile' "$fixture_root/restore-plan.out" ||
    fail "The restoration plan is missing."
grep -Fq 'only exact restored classic bytes' "$fixture_root/recover-plan.out" ||
    fail "The recovery plan is missing its exact-byte boundary."
if "$BASH" "$workflow" --activate --restore > "$fixture_root/invalid.out" 2>&1; then
    fail "Multiple operations were accepted."
fi

success="$fixture_root/success"
make_fixture "$success"
original_public="$(sha256sum "$success/config/server-public.cfg" | cut -d' ' -f1)"
original_marker="$(sha256sum "$success/config/game-event-persistent-active" | cut -d' ' -f1)"
if ! "$BASH" "$0" --case "$success" success > "$success/output" 2>&1; then
    cat "$success/output" >&2
    fail "The successful activate/upgrade/restore sequence failed."
fi
[[ "$(sha256sum "$success/config/server-public.cfg" | cut -d' ' -f1)" == "$original_public" ]] ||
    fail "The exact classic public configuration was not restored."
[[ "$(sha256sum "$success/config/game-event-persistent-active" | cut -d' ' -f1)" == "$original_marker" ]] ||
    fail "The exact classic guard marker was not restored."
[[ "$(cat "$success/service/game-event-agent/queue/receipt")" == queue-evidence ]] ||
    fail "The durable queue changed during switching."
[[ "$(cat "$success/service/server/cstrike/addons/amxmodx/data/goldsrcops-spool/receipt")" == spool-evidence ]] ||
    fail "The producer spool changed during switching."
assert_order "$success/operations.log" 'gate precheck' 'disable agent' 'disable game' \
    'stop game' 'stop agent' 'start game' 'start agent' 'gate postcheck' \
    'enable game' 'enable agent'

for failure in upgrade-postcheck upgrade-install; do
    upgrade_failed="$fixture_root/$failure"
    make_fixture "$upgrade_failed"
    if "$BASH" "$0" --case "$upgrade_failed" "$failure" > "$upgrade_failed/output" 2>&1; then
        fail "The $failure case accepted a failed loadout upgrade."
    fi
    grep -Fq 'CLASSIC_RESTORED_BOOT_DISABLED' "$upgrade_failed/output" ||
        fail "The $failure case did not restore classic."
    [[ "$(cat "$upgrade_failed/game.enabled")" == disabled &&
        "$(cat "$upgrade_failed/agent.enabled")" == disabled ]] ||
        fail "The $failure case left boot enabled."
    [[ "$(sha256sum "$upgrade_failed/config/server-public.cfg" | cut -d' ' -f1)" == \
        "$original_public" ]] || fail "The $failure case did not restore classic bytes."
    [[ ! -e "$upgrade_failed/service/server/cstrike/goldsrcops-fast-reentry-v1.cfg" ]] ||
        fail "The $failure case retained the loadout file."
    [[ "$(cat "$upgrade_failed/service/game-event-agent/queue/receipt")" == queue-evidence ]] ||
        fail "The $failure case changed the queue."
done

rejected="$fixture_root/rejected"
make_fixture "$rejected"
if "$BASH" "$0" --case "$rejected" reject-postcheck > "$rejected/output" 2>&1; then
    fail "A rejected external gate activated fast re-entry."
fi
grep -Fq 'CLASSIC_RESTORED_BOOT_DISABLED' "$rejected/output" ||
    fail "The failed gate did not restore classic."
[[ "$(cat "$rejected/game.enabled")" == disabled &&
    "$(cat "$rejected/agent.enabled")" == disabled ]] ||
    fail "A failed gate left boot enabled."
[[ "$(sha256sum "$rejected/config/server-public.cfg" | cut -d' ' -f1)" == "$original_public" ]] ||
    fail "The failed gate did not restore public configuration bytes."
[[ "$(cat "$rejected/service/game-event-agent/queue/receipt")" == queue-evidence ]] ||
    fail "A failed gate changed the queue."
[[ "$(cat "$rejected/service/server/cstrike/addons/amxmodx/data/goldsrcops-spool/receipt")" == spool-evidence ]] ||
    fail "A failed gate changed the spool."
"$BASH" "$0" --case "$rejected" recover > "$rejected/recovery-output"
grep -Fq 'CLASSIC_BOOT_RECOVERED' "$rejected/recovery-output" ||
    fail "The restored classic policy could not complete recovery."

recovery_drift="$fixture_root/recovery-drift"
make_fixture "$recovery_drift"
if "$BASH" "$0" --case "$recovery_drift" reject-postcheck > "$recovery_drift/output" 2>&1; then
    fail "The recovery drift setup did not fail at postcheck."
fi
printf 'drift\n' >> "$recovery_drift/config/server-public.cfg"
if "$BASH" "$0" --case "$recovery_drift" recover > "$recovery_drift/recover-output" 2>&1; then
    fail "Recovery accepted a drifted restored file."
fi
[[ "$(cat "$recovery_drift/game.enabled")" == disabled &&
    "$(cat "$recovery_drift/agent.enabled")" == disabled ]] ||
    fail "Recovery changed boot state despite restored-file drift."

recovery_leftover="$fixture_root/recovery-leftover"
make_fixture "$recovery_leftover"
if "$BASH" "$0" --case "$recovery_leftover" reject-postcheck > "$recovery_leftover/output" 2>&1; then
    fail "The recovery leftover setup did not fail at postcheck."
fi
printf 'unexpected profile\n' > "$recovery_leftover/service/server/cstrike/goldsrcops-fast-reentry-v1.cfg"
if "$BASH" "$0" --case "$recovery_leftover" recover > "$recovery_leftover/recover-output" 2>&1; then
    fail "Recovery accepted a leftover fast profile."
fi
[[ "$(cat "$recovery_leftover/game.enabled")" == disabled &&
    "$(cat "$recovery_leftover/agent.enabled")" == disabled ]] ||
    fail "Recovery changed boot state despite a leftover fast profile."

interrupted="$fixture_root/interrupted"
make_fixture "$interrupted"
if "$BASH" "$0" --case "$interrupted" interrupt-postcheck > "$interrupted/output" 2>&1; then
    fail "An interrupted external gate activated fast re-entry."
fi
grep -Fq 'CLASSIC_RESTORED_BOOT_DISABLED' "$interrupted/output" ||
    fail "An interrupted gate did not restore classic."

install_failure="$fixture_root/install-failure"
make_fixture "$install_failure"
if "$BASH" "$0" --case "$install_failure" reject-install > "$install_failure/output" 2>&1; then
    fail "A failed file installation activated fast re-entry."
fi
grep -Fq 'CLASSIC_RESTORED_BOOT_DISABLED' "$install_failure/output" ||
    fail "A partial installation did not restore classic."
[[ "$(sha256sum "$install_failure/config/server-public.cfg" | cut -d' ' -f1)" == "$original_public" ]] ||
    fail "A partial installation did not restore public bytes."
[[ "$(cat "$install_failure/game.enabled")" == disabled &&
    "$(cat "$install_failure/agent.enabled")" == disabled ]] ||
    fail "A partial installation left boot enabled."

timeout="$fixture_root/timeout"
make_fixture "$timeout"
if "$BASH" "$0" --gate-timeout "$timeout" > "$timeout/output" 2>&1; then
    fail "An unattended external gate passed unexpectedly."
fi
grep -Fq 'external precheck gate timed out' "$timeout/output" ||
    fail "The external gate did not fail at its timeout."
if compgen -G "$timeout/config/.fast-reentry-*-gate.*" >/dev/null; then
    fail "A timed-out external gate left a FIFO behind."
fi

for receipt_case in valid invalid restarted; do
    gate="$fixture_root/gate-$receipt_case"
    make_fixture "$gate"
    if "$BASH" "$0" --gate-receipt "$gate" "$receipt_case" > "$gate/output" 2>&1; then
        [[ "$receipt_case" == valid ]] || fail "The $receipt_case external gate passed."
    else
        [[ "$receipt_case" != valid ]] || fail "The valid external receipt was rejected."
    fi
    if compgen -G "$gate/config/.fast-reentry-*-gate.*" >/dev/null; then
        fail "The $receipt_case external gate left a FIFO behind."
    fi
done

for lock_name in game-event-pilot-install.lock managed-profile.lock; do
    locked="$fixture_root/locked-${lock_name%.lock}"
    make_fixture "$locked"
    (
        exec 6>"$locked/config/$lock_name"
        flock -x 6
        if "$BASH" "$0" --case "$locked" success > "$locked/output" 2>&1; then
            fail "The held $lock_name was ignored."
        fi
    )
    [[ "$(cat "$locked/game.enabled")" == enabled ]] ||
        fail "A failed lock acquisition changed game boot."
    [[ ! -e "$locked/config/fast-reentry-active" ]] ||
        fail "A failed lock acquisition created a transition marker."
done

drifted="$fixture_root/drifted"
make_fixture "$drifted"
if "$BASH" "$0" --case "$drifted" backup-drift > "$drifted/output" 2>&1; then
    fail "Restoration accepted a drifted backup."
fi
[[ "$(cat "$drifted/game.enabled")" == enabled &&
    "$(cat "$drifted/agent.enabled")" == enabled ]] ||
    fail "A drifted backup changed active boot state before mutation."
[[ "$(grep -c '^schema_version=2$' "$drifted/config/game-event-persistent-active")" == 1 ]] ||
    fail "A drifted backup changed the active profile."

printf 'FAST_REENTRY_TRANSITION_SMOKE=passed\n'

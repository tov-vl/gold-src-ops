#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

switch_script_path="$(realpath -e -- "${BASH_SOURCE[0]}")"
switch_script_directory="$(dirname "$switch_script_path")"
# shellcheck source=ops/gameserver/game-event-persistent.sh
source "$switch_script_directory/game-event-persistent.sh"

readonly CLASSIC_PROFILE_FILE_NAME="goldsrcops-managed-profile.cfg"
readonly SWITCH_MARKER="$configuration_directory/fast-reentry-active"
readonly SWITCH_LOCK="$configuration_directory/managed-profile.lock"
readonly SWITCH_BACKUP_ROOT="$guarded_backup_root/fast-reentry"
readonly FAST_PROFILE_SOURCE="$switch_script_directory/fast-reentry-v1.cfg"
readonly FAST_LOADOUT_PROFILE_SOURCE="$switch_script_directory/fast-reentry-loadout-v1.cfg"
readonly CLASSIC_PROFILE_FILE="$game_root/cstrike/$CLASSIC_PROFILE_FILE_NAME"

switch_operation=overview
switch_selected=false
switch_apply=false
switch_backup_name=""
switch_backup_directory=""
switch_staging_directory=""
switch_gate_fifo=""
switch_gate_timeout=300
switch_mutation_started=false

switch_usage() {
    cat <<'EOF'
Usage:
  fast-reentry-transition.sh --activate [--apply]
  fast-reentry-transition.sh --upgrade-loadout [--apply]
  fast-reentry-transition.sh --restore [--apply]
  fast-reentry-transition.sh --recover [--apply]

Without --apply, this prints a host-independent plan. Apply is restricted to
the reviewed operator via sudo with SSH_CONNECTION preserved. A root-only
external receipt is required before stopping players and again before enabling
boot after the profile switch. The operator must independently verify A2S,
zero players before mutation, and read-only RCON values at the named gates.
No identity, queue, spool, or credential file is copied or reset.
EOF
}

switch_plan() {
    log "PLAN: acquire pilot then managed-profile locks; verify exact persistent and selected-profile state"
    if [[ "$switch_operation" == recover ]]; then
        log "PLAN: accept only exact restored classic bytes with both services active and boot disabled"
        log "PLAN: require fresh external A2S/RCON evidence, then re-enable independent boot entries"
        log "PLAN_ONLY: no host state or endpoint was inspected or changed; add --apply to execute."
        return
    fi
    if [[ "$switch_operation" == upgrade-loadout ]]; then
        log "PLAN: verify the accepted fast-reentry profile and exact classic rollback backup"
        log "PLAN: require an empty aggregate and fresh external zero-player A2S/RCON precheck"
        log "PLAN: stop the game, install only the reviewed loadout revision and hash-bound guard"
        log "PLAN: restart independent services, require fresh guards and external A2S/RCON receipt, then enable boot"
        log "PLAN: on failure restore exact classic bytes, start services only if safe, and leave boot disabled"
        log "PLAN_ONLY: no host state or endpoint was inspected or changed; add --apply to execute."
        return
    fi
    log "PLAN: require an empty aggregate and fresh external zero-player A2S/RCON precheck"
    log "PLAN: preserve owner-only exact classic bytes, then disable boot and stop game before agent"
    if [[ "$switch_operation" == activate ]]; then
        log "PLAN: install the fixed fast-reentry profile and schema-2 hash-bound guard"
    else
        log "PLAN: restore the exact classic profile and schema-1 guard without touching identity, queue, or spool"
    fi
    log "PLAN: restart independent services, require fresh guards and external A2S/RCON receipt, then enable boot"
    log "PLAN: on failure restore classic bytes, start services only if safe, and leave boot disabled for inspection"
    log "PLAN_ONLY: no host state or endpoint was inspected or changed; add --apply to execute."
}

switch_select() {
    [[ "$switch_selected" == false ]] || fail "Select exactly one profile transition."
    switch_selected=true
    switch_operation="$1"
}

switch_acquire_locks() {
    acquire_lock
    exec 7>"$SWITCH_LOCK"
    flock --nonblock 7 || fail "Another managed-profile transition is in progress."
    chown root:"$service_group" "$SWITCH_LOCK"
    chmod 0640 "$SWITCH_LOCK"
}

switch_verify_classic() {
    local name classic_backup public_backup runtime_backup path
    [[ "$(marker_value "$active_profile_marker" schema_version)" == 1 &&
        "$(marker_value "$active_profile_marker" profile_id)" == "$EXPECTED_PROFILE_ID" ]] ||
        fail "The accepted classic profile is not selected."
    [[ "$(awk 'END { print NR }' "$active_profile_marker")" == 9 ]] ||
        fail "The classic profile marker has unexpected fields."
    name="$(marker_value "$active_profile_marker" backup_name)"
    [[ "$name" =~ ^managed-profile-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The classic profile rollback reference is invalid."
    classic_backup="$guarded_backup_root/$name"
    validate_directory_metadata "$classic_backup" root root 700
    public_backup="$classic_backup/server-public.cfg"
    runtime_backup="$classic_backup/runtime-enabled"
    validate_file_metadata "$public_backup" root root 600
    validate_file_metadata "$runtime_backup" root root 600
    verify_sha256 "$public_backup" "$(marker_value "$active_profile_marker" baseline_public_sha256)"
    verify_sha256 "$runtime_backup" "$(marker_value "$active_profile_marker" baseline_runtime_enabled_sha256)"

    for path in "$public_configuration" "$runtime_enabled_marker" \
        "$active_profile_marker" "$CLASSIC_PROFILE_FILE" "$mapcycle_file"; do
        validate_file_metadata "$path" root "$service_group" 640
    done
    verify_sha256 "$public_configuration" "$(marker_value "$active_profile_marker" public_sha256)"
    verify_sha256 "$runtime_enabled_marker" "$(marker_value "$active_profile_marker" runtime_enabled_sha256)"
    verify_sha256 "$CLASSIC_PROFILE_FILE" "$(marker_value "$active_profile_marker" profile_sha256)"
    verify_sha256 "$mapcycle_file" "$(marker_value "$active_profile_marker" mapcycle_sha256)"
    [[ "$(marker_value "$runtime_enabled_marker" public_config_sha256)" == \
        "$(sha256_file "$public_configuration")" ]] ||
        fail "The classic runtime marker does not bind the public configuration."
    [[ "$(grep -Fxc "exec $CLASSIC_PROFILE_FILE_NAME" "$public_configuration")" == 1 ]] ||
        fail "The classic profile is not loaded exactly once."
    grep -Fxq "mapchangecfgfile \"$CLASSIC_PROFILE_FILE_NAME\"" "$CLASSIC_PROFILE_FILE" ||
        fail "The classic map-change hook has drifted."
}

switch_verify_source() {
    validate_reviewed_script "$switch_script_path"
    validate_reviewed_script "$script_path"
    verify_sha256 "$FAST_PROFILE_SOURCE" "$FAST_PROFILE_SHA256"
    [[ ! -L "$FAST_PROFILE_SOURCE" ]] || fail "The candidate profile path is unsafe."
    if [[ "$switch_operation" == upgrade-loadout ]]; then
        verify_sha256 "$FAST_LOADOUT_PROFILE_SOURCE" "$FAST_LOADOUT_PROFILE_SHA256"
        [[ ! -L "$FAST_LOADOUT_PROFILE_SOURCE" ]] || fail "The loadout profile path is unsafe."
    fi
}

switch_verify_backup() {
    local record="$switch_backup_directory/baseline"
    local file key mode group
    validate_directory_metadata "$SWITCH_BACKUP_ROOT" root root 700
    validate_directory_metadata "$switch_backup_directory" root root 700
    validate_file_metadata "$record" root root 600
    [[ "$(marker_value "$record" schema_version)" == 1 &&
        "$(marker_value "$record" backup_name)" == "$switch_backup_name" &&
        "$(awk 'END { print NR }' "$record")" == 9 ]] ||
        fail "The switch backup contract is invalid."
    for file in server-public.cfg runtime-enabled managed-profile-active \
        classic-profile.cfg mapcycle.txt persistent-marker persistent-guard; do
        key="${file//[.-]/_}_sha256"
        mode=640
        group="$service_group"
        if [[ "$file" == persistent-guard ]]; then
            mode=755
            group=root
        fi
        validate_file_metadata "$switch_backup_directory/$file" root "$group" "$mode" \
            || return 1
        verify_sha256 "$switch_backup_directory/$file" "$(marker_value "$record" "$key")" || return 1
    done
}

switch_capture_backup() {
    local file key i
    validate_directory_metadata "$guarded_backup_root" root root 700
    if [[ -e "$SWITCH_BACKUP_ROOT" || -L "$SWITCH_BACKUP_ROOT" ]]; then
        validate_directory_metadata "$SWITCH_BACKUP_ROOT" root root 700
    else
        install -d -m 0700 -o root -g root "$SWITCH_BACKUP_ROOT"
    fi
    validate_directory_metadata "$SWITCH_BACKUP_ROOT" root root 700
    switch_backup_directory="$(mktemp -d "$SWITCH_BACKUP_ROOT/fast-reentry-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
    switch_backup_name="$(basename "$switch_backup_directory")"
    chmod 0700 "$switch_backup_directory"
    local -a sources=("$public_configuration" "$runtime_enabled_marker" "$active_profile_marker"
        "$CLASSIC_PROFILE_FILE" "$mapcycle_file" "$persistent_marker" "$installed_persistent_guard")
    local -a names=(server-public.cfg runtime-enabled managed-profile-active
        classic-profile.cfg mapcycle.txt persistent-marker persistent-guard)
    for ((i = 0; i < ${#sources[@]}; i++)); do
        cp -a -- "${sources[i]}" "$switch_backup_directory/${names[i]}"
    done
    {
        printf 'schema_version=1\nbackup_name=%s\n' "$switch_backup_name"
        for file in "${names[@]}"; do
            key="${file//[.-]/_}_sha256"
            printf '%s=%s\n' "$key" "$(sha256_file "$switch_backup_directory/$file")"
        done
    } > "$switch_backup_directory/baseline"
    chmod 0600 "$switch_backup_directory/baseline"
    switch_verify_backup
}

switch_load_backup() {
    validate_file_metadata "$SWITCH_MARKER" root "$service_group" 640
    [[ "$(marker_value "$SWITCH_MARKER" schema_version)" == 1 &&
        "$(awk 'END { print NR }' "$SWITCH_MARKER")" == 2 ]] ||
        fail "The active switch marker is invalid."
    switch_backup_name="$(marker_value "$SWITCH_MARKER" backup_name)"
    [[ "$switch_backup_name" =~ ^fast-reentry-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The switch backup reference is invalid."
    switch_backup_directory="$SWITCH_BACKUP_ROOT/$switch_backup_name"
    switch_verify_backup
}

switch_verify_restored_bytes() {
    local file current
    for file in server-public.cfg runtime-enabled managed-profile-active \
        classic-profile.cfg mapcycle.txt persistent-marker persistent-guard; do
        case "$file" in
            server-public.cfg) current="$public_configuration" ;;
            runtime-enabled) current="$runtime_enabled_marker" ;;
            managed-profile-active) current="$active_profile_marker" ;;
            classic-profile.cfg) current="$CLASSIC_PROFILE_FILE" ;;
            mapcycle.txt) current="$mapcycle_file" ;;
            persistent-marker) current="$persistent_marker" ;;
            persistent-guard) current="$installed_persistent_guard" ;;
        esac
        cmp -s -- "$switch_backup_directory/$file" "$current" ||
            fail "A restored classic file does not match the owner-only backup."
    done
}

switch_write_marker() {
    local temporary="$configuration_directory/.fast-reentry-active.$$"
    printf 'schema_version=1\nbackup_name=%s\n' "$switch_backup_name" > "$temporary"
    chown root:"$service_group" "$temporary"
    chmod 0640 "$temporary"
    mv -- "$temporary" "$SWITCH_MARKER"
}

switch_prepare_staging() {
    local public_hash runtime_hash profile_hash
    switch_staging_directory="$(mktemp -d "$configuration_directory/.fast-reentry.XXXXXX")"
    chmod 0700 "$switch_staging_directory"
    profile_hash="$(sha256_file "$FAST_PROFILE_SOURCE")"
    [[ "$profile_hash" == "$FAST_PROFILE_SHA256" ]] || fail "The candidate profile has drifted."
    [[ "$(grep -Fxc "exec $CLASSIC_PROFILE_FILE_NAME" "$public_configuration")" == 1 ]] ||
        fail "The classic load line is missing or duplicated."
    awk -v classic="exec $CLASSIC_PROFILE_FILE_NAME" -v fast="exec $FAST_PROFILE_FILE_NAME" \
        '$0 == classic { print fast; next } { print }' "$public_configuration" > \
        "$switch_staging_directory/server-public.cfg"
    public_hash="$(sha256_file "$switch_staging_directory/server-public.cfg")"
    [[ "$(grep -Ec '^public_config_sha256=' "$runtime_enabled_marker")" == 1 ]] ||
        fail "The runtime-enabled public hash is missing or duplicated."
    awk -v hash="$public_hash" \
        '/^public_config_sha256=/ { print "public_config_sha256=" hash; next } { print }' \
        "$runtime_enabled_marker" > "$switch_staging_directory/runtime-enabled"
    runtime_hash="$(sha256_file "$switch_staging_directory/runtime-enabled")"
    awk -v public_hash="$public_hash" -v runtime_hash="$runtime_hash" \
        -v profile_hash="$profile_hash" '
        /^profile_id=/ { print "profile_id=fast-reentry-v1"; next }
        /^public_sha256=/ { print "public_sha256=" public_hash; next }
        /^runtime_enabled_sha256=/ { print "runtime_enabled_sha256=" runtime_hash; next }
        /^profile_sha256=/ { print "profile_sha256=" profile_hash; next }
        { print }
    ' "$active_profile_marker" > "$switch_staging_directory/managed-profile-active"
    awk -v guard_hash="$(sha256_file "$script_path")" '
        /^schema_version=/ { print "schema_version=2"; next }
        /^guard_sha256=/ { print "guard_sha256=" guard_hash; next }
        { print }
    ' "$persistent_marker" > "$switch_staging_directory/persistent-marker"
    cat >> "$switch_staging_directory/persistent-marker" <<EOF
profile_id=$FAST_PROFILE_ID
public_sha256=$public_hash
runtime_enabled_sha256=$runtime_hash
active_profile_sha256=$(sha256_file "$switch_staging_directory/managed-profile-active")
profile_sha256=$profile_hash
mapcycle_sha256=$(sha256_file "$mapcycle_file")
EOF
}

switch_prepare_loadout_staging() {
    local profile_hash
    switch_staging_directory="$(mktemp -d "$configuration_directory/.fast-reentry.XXXXXX")"
    chmod 0700 "$switch_staging_directory"
    profile_hash="$(sha256_file "$FAST_LOADOUT_PROFILE_SOURCE")"
    [[ "$profile_hash" == "$FAST_LOADOUT_PROFILE_SHA256" ]] ||
        fail "The loadout profile has drifted."
    awk -v profile_hash="$profile_hash" '
        /^profile_sha256=/ { print "profile_sha256=" profile_hash; next }
        { print }
    ' "$active_profile_marker" > "$switch_staging_directory/managed-profile-active"
    awk -v guard_hash="$(sha256_file "$script_path")" \
        -v active_hash="$(sha256_file "$switch_staging_directory/managed-profile-active")" \
        -v profile_hash="$profile_hash" '
        /^guard_sha256=/ { print "guard_sha256=" guard_hash; next }
        /^active_profile_sha256=/ { print "active_profile_sha256=" active_hash; next }
        /^profile_sha256=/ { print "profile_sha256=" profile_hash; next }
        { print }
    ' "$persistent_marker" > "$switch_staging_directory/persistent-marker"
}

switch_install_fast() {
    install -o root -g "$service_group" -m 0640 "$FAST_PROFILE_SOURCE" "$fast_profile_file"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/server-public.cfg" "$public_configuration"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/runtime-enabled" "$runtime_enabled_marker"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/managed-profile-active" "$active_profile_marker"
    install -o root -g root -m 0755 "$script_path" "$installed_persistent_guard"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/persistent-marker" "$persistent_marker"
    verify_persistent_files
}

switch_install_loadout() {
    install -o root -g "$service_group" -m 0640 \
        "$FAST_LOADOUT_PROFILE_SOURCE" "$fast_profile_file"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/managed-profile-active" "$active_profile_marker"
    install -o root -g root -m 0755 "$script_path" "$installed_persistent_guard"
    install -o root -g "$service_group" -m 0640 \
        "$switch_staging_directory/persistent-marker" "$persistent_marker"
    verify_persistent_files
}

switch_install_classic_files() {
    switch_verify_backup || return 1

    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/server-public.cfg" "$public_configuration" || return 1
    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/runtime-enabled" "$runtime_enabled_marker" || return 1
    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/managed-profile-active" "$active_profile_marker" || return 1
    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/classic-profile.cfg" "$CLASSIC_PROFILE_FILE" || return 1
    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/mapcycle.txt" "$mapcycle_file" || return 1
    install -o root -g root -m 0755 \
        "$switch_backup_directory/persistent-guard" "$installed_persistent_guard" || return 1
    install -o root -g "$service_group" -m 0640 \
        "$switch_backup_directory/persistent-marker" "$persistent_marker" || return 1
    rm -f -- "$fast_profile_file" || return 1
    verify_persistent_files || return 1
    switch_verify_classic || return 1
}

switch_restore_classic() {
    local failed=false
    systemctl disable "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || failed=true
    systemctl disable "$GAME_SERVICE_NAME" >/dev/null 2>&1 || failed=true
    systemctl stop "$GAME_SERVICE_NAME" >/dev/null 2>&1 || failed=true
    systemctl stop "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || failed=true
    [[ "$failed" == false ]] || return 1
    switch_install_classic_files || return 1
    systemctl start "$GAME_SERVICE_NAME" || return 1
    systemctl start "$AGENT_SERVICE_NAME" || return 1
    require_unit_state "$GAME_SERVICE_NAME" active disabled || return 1
    require_unit_state "$AGENT_SERVICE_NAME" active disabled || return 1
    capture_agent_status >/dev/null || return 1
}

switch_failed() {
    local code="$1"
    ((code != 0)) || code=1
    trap - EXIT HUP INT TERM
    if [[ "$switch_mutation_started" == true ]]; then
        log "ROLLBACK: restoring the exact classic profile without resetting telemetry state."
        if switch_restore_classic; then
            log "CLASSIC_RESTORED_BOOT_DISABLED: independent services are active; inspect external A2S before enabling boot."
        else
            systemctl disable "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || true
            systemctl disable "$GAME_SERVICE_NAME" >/dev/null 2>&1 || true
            systemctl stop "$GAME_SERVICE_NAME" >/dev/null 2>&1 || true
            systemctl stop "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || true
            log "ROLLBACK_INCOMPLETE: service state is uncertain; preserve the owner-only backup for manual recovery."
        fi
    fi
    if [[ -n "$switch_gate_fifo" ]]; then
        rm -f -- "$switch_gate_fifo" || true
    fi
    if [[ -n "$switch_staging_directory" && -d "$switch_staging_directory" ]]; then
        rm -rf -- "$switch_staging_directory" || true
    fi
    exit "$code"
}

switch_gate() {
    local name="$1" expected_invocation nonce receipt=""
    expected_invocation="$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)"
    [[ "$expected_invocation" =~ ^[0-9a-fA-F]{32}$ ]] ||
        fail "The game invocation is invalid."
    nonce="$(od -An -N16 -tx1 /dev/urandom | tr -d '[:space:]')"
    [[ "$nonce" =~ ^[0-9a-f]{32}$ ]] || fail "The gate challenge is invalid."
    switch_gate_fifo="$configuration_directory/.fast-reentry-$name-gate.$$"
    [[ ! -e "$switch_gate_fifo" && ! -L "$switch_gate_fifo" ]] ||
        fail "The external gate path is occupied."
    mkfifo -m 0600 -- "$switch_gate_fifo"
    log "EXTERNAL_${name^^}_GATE_READY: $nonce $switch_gate_fifo"
    exec 8<>"$switch_gate_fifo"
    if ! IFS= read -r -t "$switch_gate_timeout" -u 8 receipt; then
        exec 8>&-
        fail "The external $name gate timed out."
    fi
    exec 8>&-
    rm -f -- "$switch_gate_fifo"
    switch_gate_fifo=""
    [[ "$receipt" == "$nonce" ]] || fail "The external $name receipt is invalid."
    [[ "$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)" == \
        "$expected_invocation" ]] || fail "The game restarted during the external gate."
    require_settled_agent_status
    if [[ "$name" == precheck ]]; then
        require_unit_state "$GAME_SERVICE_NAME" active enabled
        require_unit_state "$AGENT_SERVICE_NAME" active enabled
    else
        require_unit_state "$GAME_SERVICE_NAME" active disabled
        require_unit_state "$AGENT_SERVICE_NAME" active disabled
    fi
    log "EXTERNAL_${name^^}_GATE=operator-attested"
}

switch_check_started() {
    local game_invocation="$1" agent_invocation="$2" service invocation
    for service in "$GAME_SERVICE_NAME" "$AGENT_SERVICE_NAME"; do
        require_unit_state "$service" active disabled
        [[ "$(systemctl show "$service" -p NRestarts --value)" == 0 ]] ||
            fail "A switched service restarted unexpectedly."
    done
    invocation="$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)"
    [[ "$invocation" =~ ^[0-9a-fA-F]{32}$ && "$invocation" != "$game_invocation" ]] ||
        fail "The game did not start in a fresh invocation."
    invocation="$(systemctl show "$AGENT_SERVICE_NAME" -p InvocationID --value)"
    [[ "$invocation" =~ ^[0-9a-fA-F]{32}$ && "$invocation" != "$agent_invocation" ]] ||
        fail "The agent did not start in a fresh invocation."
    runuser -u "$service_user" -- "$installed_persistent_guard" --guard-game >/dev/null
    "$installed_persistent_guard" --guard-agent >/dev/null
    verify_persistent_files
    require_settled_agent_status
}

switch_wait_for_settled_agent() {
    local attempt
    for ((attempt = 1; attempt <= 30; attempt++)); do
        if require_settled_agent_status >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    fail "The agent did not settle after producer intake stopped."
}

switch_start_transition() {
    local game_invocation agent_invocation
    game_invocation="$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)"
    agent_invocation="$(systemctl show "$AGENT_SERVICE_NAME" -p InvocationID --value)"
    switch_mutation_started=true
    trap 'switch_failed $?' EXIT
    trap 'exit 129' HUP
    trap 'exit 130' INT
    trap 'exit 143' TERM
    if [[ "$switch_operation" == activate ]]; then
        switch_write_marker
    fi
    systemctl disable "$AGENT_SERVICE_NAME" >/dev/null
    systemctl disable "$GAME_SERVICE_NAME" >/dev/null
    systemctl stop "$GAME_SERVICE_NAME"
    switch_wait_for_settled_agent
    systemctl stop "$AGENT_SERVICE_NAME"
    require_unit_state "$GAME_SERVICE_NAME" inactive disabled
    require_unit_state "$AGENT_SERVICE_NAME" inactive disabled
    case "$switch_operation" in
        activate) switch_install_fast ;;
        upgrade-loadout) switch_install_loadout ;;
        restore) switch_install_classic_files ;;
    esac
    systemctl start "$GAME_SERVICE_NAME"
    systemctl start "$AGENT_SERVICE_NAME"
    switch_check_started "$game_invocation" "$agent_invocation"
    switch_gate postcheck
    systemctl enable "$GAME_SERVICE_NAME" >/dev/null
    systemctl enable "$AGENT_SERVICE_NAME" >/dev/null
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" active enabled
    if [[ "$switch_operation" == restore ]]; then
        printf 'schema_version=1\nrestoration=complete\n' > "$switch_backup_directory/restore-complete"
        chmod 0600 "$switch_backup_directory/restore-complete"
        rm -f -- "$SWITCH_MARKER"
        log "CLASSIC_PROFILE_RESTORED: exact prior bytes and independent boot entries verified."
    elif [[ "$switch_operation" == upgrade-loadout ]]; then
        log "FAST_REENTRY_LOADOUT_ACTIVATED: hash-bound loadout and independent boot entries verified."
    else
        log "FAST_REENTRY_ACTIVATED: hash-bound profile and independent boot entries verified."
    fi
    switch_mutation_started=false
    trap - EXIT HUP INT TERM
    rm -rf -- "$switch_staging_directory"
}

switch_run_recover() {
    require_command cmp
    [[ "$marker_schema_version" == 1 ]] || fail "Classic policy has not been restored."
    [[ ! -e "$fast_profile_file" && ! -L "$fast_profile_file" ]] ||
        fail "The fast-reentry profile remains installed."
    switch_load_backup
    switch_verify_classic
    switch_verify_restored_bytes
    require_unit_state "$GAME_SERVICE_NAME" active disabled
    require_unit_state "$AGENT_SERVICE_NAME" active disabled
    require_settled_agent_status
    trap 'switch_failed $?' EXIT
    trap 'exit 129' HUP
    trap 'exit 130' INT
    trap 'exit 143' TERM
    switch_gate postcheck
    switch_mutation_started=true
    systemctl enable "$GAME_SERVICE_NAME" >/dev/null
    systemctl enable "$AGENT_SERVICE_NAME" >/dev/null
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" active enabled
    printf 'schema_version=1\nrestoration=complete\n' > "$switch_backup_directory/restore-complete"
    chmod 0600 "$switch_backup_directory/restore-complete"
    rm -f -- "$SWITCH_MARKER"
    switch_mutation_started=false
    trap - EXIT HUP INT TERM
    log "CLASSIC_BOOT_RECOVERED: exact prior files and external gate verified."
}

switch_apply_operation() {
    require_apply_environment
    switch_verify_source
    switch_acquire_locks
    verify_persistent_files
    verify_baseline_record
    if [[ "$switch_operation" == recover ]]; then
        switch_run_recover
        return
    fi
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" active enabled
    require_settled_agent_status
    if [[ "$switch_operation" == activate ]]; then
        [[ "$marker_schema_version" == 1 && ! -e "$SWITCH_MARKER" && ! -L "$SWITCH_MARKER" &&
            ! -e "$fast_profile_file" && ! -L "$fast_profile_file" ]] ||
            fail "A fast-reentry transition or recovery boundary already exists."
        switch_verify_classic
    elif [[ "$switch_operation" == upgrade-loadout ]]; then
        [[ "$marker_schema_version" == 2 &&
            "$marker_profile_sha256" == "$FAST_PROFILE_SHA256" ]] ||
            fail "The accepted fast-reentry profile is not active."
        switch_load_backup
    else
        [[ "$marker_schema_version" == 2 ]] || fail "Fast re-entry is not active."
        switch_load_backup
    fi
    trap 'switch_failed $?' EXIT
    trap 'exit 129' HUP
    trap 'exit 130' INT
    trap 'exit 143' TERM
    switch_gate precheck
    if [[ "$switch_operation" == activate ]]; then
        switch_capture_backup
        switch_prepare_staging
    elif [[ "$switch_operation" == upgrade-loadout ]]; then
        switch_prepare_loadout_staging
    else
        switch_staging_directory="$(mktemp -d "$configuration_directory/.fast-reentry.XXXXXX")"
        chmod 0700 "$switch_staging_directory"
    fi
    switch_start_transition
}

switch_main() {
    while (($# > 0)); do
        case "$1" in
            --activate) switch_select activate ;;
            --upgrade-loadout) switch_select upgrade-loadout ;;
            --restore) switch_select restore ;;
            --recover) switch_select recover ;;
            --apply) switch_apply=true ;;
            -h|--help) switch_usage; return ;;
            *) fail "Unknown profile transition argument." ;;
        esac
        shift
    done
    [[ "$switch_selected" == true ]] ||
        fail "Select --activate, --upgrade-loadout, --restore, or --recover."
    if [[ "$switch_apply" == true ]]; then
        switch_apply_operation
    else
        switch_plan
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    switch_main "$@"
fi

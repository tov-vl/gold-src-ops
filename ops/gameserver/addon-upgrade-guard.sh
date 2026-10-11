#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID == 0 && $# == 1 ]]
directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$directory/game-event-persistent.sh"
case "$1" in
    identity)
        read_prepared_marker
        [[ "${SUDO_USER:-}" == "$prepared_operator_user" && -n "${SSH_CONNECTION:-}" ]]
        ;;
    preflight|baseline)
        bash "$directory/player-data-schedule-guard.sh" preflight
        bash "$directory/player-data-schedule-guard.sh" postflight
        ;;
    running)
        verify_persistent_files
        require_unit_state "$GAME_SERVICE_NAME" active enabled
        require_unit_state "$AGENT_SERVICE_NAME" active enabled
        require_settled_agent_status
        ;;
    quiescent)
        verify_persistent_files
        [[ "$(systemctl show "$GAME_SERVICE_NAME" -p ActiveState --value)" == inactive ]]
        [[ "$(systemctl show "$GAME_SERVICE_NAME" -p MainPID --value)" == 0 ]]
        result=0
        pgrep -u "$service_user" -x hlds_linux >/dev/null || result=$?
        [[ $result == 1 ]]
        validate_directory_metadata "$live_amxx_root/data/vault" "$service_user" "$service_group" 700
        ;;
    *) exit 2 ;;
esac

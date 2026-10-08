#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID == 0 && $# == 1 ]] || exit 1
directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=ops/gameserver/game-event-persistent.sh
source "$directory/game-event-persistent.sh"
verify_persistent_files
baseline="$configuration_directory/player-data-backup-baseline.sha256"
validate_file_metadata "$baseline" root root 600
[[ $(stat -c %s "$baseline") -le 65536 ]]
sha256sum --check --status "$baseline"
case "$1" in
    endpoint)
        port="$(marker_value "$prepared_marker" game_port)"
        [[ "$port" =~ ^[0-9]{1,5}$ && $port -gt 0 && $port -le 65535 ]]
        printf '%s\n' "$port"
        ;;
    preflight|postflight)
        require_unit_state "$GAME_SERVICE_NAME" active enabled
        require_unit_state "$AGENT_SERVICE_NAME" active enabled
        require_settled_agent_status
        validate_directory_metadata "$live_amxx_root/data/vault" "$service_user" "$service_group" 700
        ;;
    quiescent)
        [[ "$(systemctl show "$GAME_SERVICE_NAME" -p ActiveState --value)" == inactive ]]
        [[ "$(systemctl show "$GAME_SERVICE_NAME" -p MainPID --value)" == 0 ]]
        result=0
        pgrep -u "$service_user" -x hlds_linux >/dev/null || result=$?
        [[ $result == 1 ]]
        ;;
    *) exit 2 ;;
esac

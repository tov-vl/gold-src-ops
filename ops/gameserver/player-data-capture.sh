#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

# Run only inside an already reviewed maintenance window. This command never
# stops, starts or restores the game service, and never reads its credentials.
[[ $# == 1 && "$1" == /* ]] || { echo 'Usage: player-data-capture.sh /absolute/new-bundle.tar' >&2; exit 2; }
[[ $EUID == 0 ]] || { echo 'Root is required for the host guards.' >&2; exit 1; }
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=ops/gameserver/game-event-persistent.sh
source "$script_directory/game-event-persistent.sh"
exec 8>"$configuration_directory/player-data-backup.lock"
flock --nonblock 8
exec 9>"$transition_lock"
flock --nonblock 9
verify_persistent_files

require_quiescent() {
    [[ "$(systemctl show "$GAME_SERVICE_NAME" -p ActiveState --value)" == inactive ]]
    [[ "$(systemctl show "$GAME_SERVICE_NAME" -p MainPID --value)" == 0 ]]
    local status=0
    pgrep -u "$service_user" -x hlds_linux >/dev/null || status=$?
    [[ $status == 1 ]]
}

require_quiescent
validate_directory_metadata "$live_amxx_root/data/vault" "$service_user" "$service_group" 700
python3 "$script_directory/player-data-bundle.py" create \
    --vault-directory "$live_amxx_root/data/vault" --output "$1"
require_quiescent
echo PLAYER_DATA_CAPTURE=passed

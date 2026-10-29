#!/usr/bin/env bash
set -Eeuo pipefail
# Install this exact file root-owned at the fixed dispatcher path.
dispatcher=/usr/local/libexec/goldsrcops-player-data-backup
if [[ $EUID != 0 ]]; then
    if [[ $# == 0 && "${SSH_ORIGINAL_COMMAND:-}" == agent-status ]]; then
        exec sudo -n -- "$dispatcher" agent-status
    fi
    [[ $# == 0 && "${SSH_ORIGINAL_COMMAND:-}" =~ ^(capture|status|bundle)\ ([0-9]{4}-[0-9]{2}-[0-9]{2})$ ]] || exit 2
    exec sudo -n -- "$dispatcher" "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}"
fi
if [[ $# == 1 && "$1" == agent-status ]]; then
    exec /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin \
        /usr/bin/python3 -I -B /opt/goldsrcops-agent-observation/ops/gameserver/agent-status.py
fi
[[ $# == 2 && "$1" =~ ^(capture|status|bundle)$ && "$2" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || exit 2
exec /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin \
    /usr/bin/python3 -I /opt/goldsrcops-player-backup/ops/gameserver/player-data-scheduled-capture.py "$1" "$2"

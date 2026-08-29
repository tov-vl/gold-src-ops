#!/bin/sh
set -eu

database_connection_file="/run/secrets/database-connection"
rcon_password_file="/run/secrets/rcon-password"
rcon_secret_alias="${GOLDSRCOPS_RCON_SECRET_ALIAS:-}"

require_readable_secret() {
    secret_path="$1"
    secret_name="$2"

    if [ ! -r "$secret_path" ] || [ ! -s "$secret_path" ]; then
        echo "Required $secret_name secret is missing or empty." >&2
        exit 1
    fi
}

require_readable_secret "$database_connection_file" "database connection"
require_readable_secret "$rcon_password_file" "RCON password"

case "$rcon_secret_alias" in
    ""|*[!A-Za-z0-9._-]*|[!A-Za-z0-9]*|*[!A-Za-z0-9])
        echo "GOLDSRCOPS_RCON_SECRET_ALIAS is invalid." >&2
        exit 1
        ;;
esac

if [ "${#rcon_secret_alias}" -gt 128 ]; then
    echo "GOLDSRCOPS_RCON_SECRET_ALIAS is too long." >&2
    exit 1
fi

database_connection="$(cat "$database_connection_file")"
rcon_password="$(cat "$rcon_password_file")"

case "$rcon_password" in
    *\"*)
        echo "The RCON password must not contain a double quote." >&2
        exit 1
        ;;
esac

export ConnectionStrings__GoldSrcOps="$database_connection"
export "RconSecrets__${rcon_secret_alias}=$rcon_password"

exec dotnet GoldSrcOps.Api.dll

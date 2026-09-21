#!/bin/sh
set -eu

database_connection_file="/run/secrets/receiver-database-connection"
receiver_authorization_file="/run/secrets/receiver-authorization"
provider_authorization_file="/run/secrets/provider-authorization"
provider_endpoint_file="/run/secrets/provider-endpoint"
provider_operations_authorization_file="/run/secrets/provider-operations-authorization"

require_readable_secret() {
    secret_path="$1"
    secret_name="$2"

    if [ ! -r "$secret_path" ] || [ ! -s "$secret_path" ]; then
        echo "Required $secret_name secret is missing or empty." >&2
        exit 1
    fi
}

require_single_line_secret() {
    secret_value="$1"
    secret_name="$2"

    case "$secret_value" in
        *"
"*)
            echo "$secret_name secret must contain exactly one line." >&2
            exit 1
            ;;
    esac
}

require_readable_secret "$database_connection_file" "receiver database connection"
database_connection="$(cat "$database_connection_file")"
require_single_line_secret "$database_connection" "Receiver database connection"
export ConnectionStrings__AlertReceiver="$database_connection"

if [ "${1:-}" = "migrate" ]; then
    shift
    exec /app/goldsrcops-alert-receiver-migrate "$@"
fi

require_readable_secret "$receiver_authorization_file" "receiver authorization"
receiver_authorization="$(cat "$receiver_authorization_file")"
require_single_line_secret "$receiver_authorization" "Receiver authorization"
export Receiver__Authorization="$receiver_authorization"

case "${ProviderDelivery__Enabled:-false}" in
    true|True|TRUE|1)
        require_readable_secret "$provider_endpoint_file" "provider endpoint"
        provider_endpoint="$(cat "$provider_endpoint_file")"
        require_single_line_secret "$provider_endpoint" "Provider endpoint"
        export ProviderDelivery__Endpoint="$provider_endpoint"

        require_readable_secret "$provider_authorization_file" "provider authorization"
        provider_authorization="$(cat "$provider_authorization_file")"
        require_single_line_secret "$provider_authorization" "Provider authorization"
        export ProviderDelivery__Authorization="$provider_authorization"
        ;;
    false|False|FALSE|0)
        ;;
    *)
        echo "ProviderDelivery__Enabled must be true or false." >&2
        exit 1
        ;;
esac

case "${ProviderOperations__Enabled:-false}" in
    true|True|TRUE|1)
        require_readable_secret "$provider_operations_authorization_file" "provider operations authorization"
        provider_operations_authorization="$(cat "$provider_operations_authorization_file")"
        require_single_line_secret "$provider_operations_authorization" "Provider operations authorization"
        if [ "${#provider_operations_authorization}" -gt 8192 ]; then
            echo "Provider operations authorization secret must not exceed 8192 characters." >&2
            exit 1
        fi
        export ProviderOperations__Authorization="$provider_operations_authorization"
        ;;
    false|False|FALSE|0)
        ;;
    *)
        echo "ProviderOperations__Enabled must be true or false." >&2
        exit 1
        ;;
esac

exec dotnet GoldSrcOps.AlertReceiver.dll "$@"

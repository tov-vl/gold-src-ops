# v2.115.0 - Minute-precision credential expiry

Status: implementation and release verification are in progress; not released.

Operators can distinguish a recorded UTC minute from an exact-second expiry.
Minute entries carry no invented seconds, use a conservative warning boundary
and retain the timestamp of the actual provider check. Malformed or ambiguous
metadata remains Unknown; the existing Grafana second-precision behavior stays
compatible. Old Web degrades minute entries to Unknown.

This API/Web-only slice prepares the checked B2 metadata for the private
register. It does not rotate keys, change their permissions, update GitHub
secrets, pause the archive scheduler or test live credential validity. See
[scope, evidence and limits](v2.115-minute-credential-expiry.md).

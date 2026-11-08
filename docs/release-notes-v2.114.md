# v2.114.0 - Credential expiry metadata

Status: implementation and release gates are in progress; not yet released.

Operators gain a private read-only register of expiry and last-verification
dates for four existing service-access purposes. Exact deadline, seven-day and
30-day warnings are distinct from Unknown metadata. Labels are fixed; values,
identifiers and secret references are excluded. Dates describe a recorded check,
not current provider validity or successful rotation.

The page works without JavaScript through the existing server-side BFF and
Operator policy. Only API/Web and their date-only configuration mapping change.
Database, game, agent, monitoring rules, IRM/email, provider settings and
permissions are unchanged. See [scope and gates](v2.114-credential-expiry.md).

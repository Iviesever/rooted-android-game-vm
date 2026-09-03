# Security policy

## Supported version

Security fixes are applied to the latest source on `main` and to the latest published Release when one exists.

## Reporting a vulnerability

Use the repository's private GitHub Security Advisory reporting flow. Do not publish credentials, signing material, private application data or an unpatched exploit in a public issue.

Include the affected commit or version, reproduction conditions, expected impact and any proposed mitigation. Reports involving unauthorized access to third-party systems or data are outside the project's intended use.

## Release integrity

Official releases must satisfy `CODE_SIGNING_POLICY.md`. Version 0.1.1 is deliberately and visibly unsigned: use only the `UNSIGNED` installer attached to this repository's Release and verify its SHA-256 or GitHub provenance before execution.

# Security policy

Replica processes untrusted archives and can apply explicitly approved Windows changes. Please report security problems carefully and do not expose users while a fix is being prepared.

## Supported versions

Replica has not published a Stable Release. Pre-release builds are for testing and receive fixes on the latest maintained pre-release line on a best-effort basis. Once Stable Releases begin, this table will identify the supported release lines.

| Version | Supported |
| --- | --- |
| Latest source on the default branch | Development testing only |
| Alpha, Beta, or RC Release | Best effort until superseded |
| Stable Release | Not available yet |

## Reporting a vulnerability

Do not open a public issue and do not attach a malicious `.replica` file, credentials, logs containing private data, or affected user files to an issue or pull request.

Use GitHub's **Privately report a security vulnerability** action on the repository Security page. Do not open a public issue for a suspected vulnerability. If Private Vulnerability Reporting is temporarily unavailable, contact the repository owner, [HechoLP](https://github.com/HechoLP), through a private contact method published on that profile and initially include only a minimal, synthetic reproduction.

Useful initial information includes:

- affected commit or exact Release tag;
- Windows version and architecture;
- the security boundary involved;
- minimal reproduction steps using synthetic data;
- expected and observed result;
- whether exploitation requires user approval or administrator elevation;
- a proposed mitigation, if known.

Do not send secrets or real user data. Remove usernames and paths, and replace sensitive archive contents with inert fixtures.

## In scope

Reports are especially useful for Snapshot traversal or extraction bypasses, decompression or parser resource-limit bypasses, checksum or encryption failures, elevated-plan tampering or replay, arbitrary command or argument injection, unsafe file restoration, reparse-point or TOCTOU issues, sensitive data capture or logging, rollback integrity failures, and GitHub Release Asset validation bypasses.

## Coordinated disclosure

Please allow time to reproduce, assess, and prepare a fix before public disclosure. The owner will acknowledge receipt when possible, communicate material status changes, and credit the reporter if requested and appropriate. No response-time or bounty guarantee is currently offered.

Do not test against another person's machine or data, disrupt GitHub or package services, install software without authorization, or retain private data obtained during research.

## Security design

The repository documents its [security model](docs/SECURITY.md), [threat model](docs/THREAT_MODEL.md), and [security test matrix](docs/SECURITY_TESTING.md). These documents describe intended boundaries, not a guarantee that the software is free of vulnerabilities.

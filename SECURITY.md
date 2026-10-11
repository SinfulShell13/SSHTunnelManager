# Security policy

## Reporting a vulnerability

Please do not publish private keys, passwords, host names, or complete SSH configurations in a public issue. Report suspected vulnerabilities privately through the repository's GitHub Security Advisories once the repository is published. Include the affected version, Windows/.NET version, reproduction steps, and any relevant log excerpt with secrets removed.

This application invokes the system OpenSSH client and reads the user's SSH configuration. Keep host key verification enabled and use `BatchMode=yes` credentials that do not require interactive input for unattended tunnels.

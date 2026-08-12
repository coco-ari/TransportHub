# Security Policy

TransportHub is intended for a group of Windows computers owned and managed by
the same trusted person. A device with write access to the shared Syncthing
folder can write TransportHub message and receipt metadata.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature for this
repository. Do not disclose API keys, Syncthing certificates, private keys,
personal paths, device IDs, or exploitable details in a public issue.

Include the affected version or commit, reproduction steps, expected impact,
and any suggested mitigation. You should receive an acknowledgement after the
report has been reviewed.

## Not security vulnerabilities

- Syncthing relay metadata exposure documented by Syncthing.
- Data loss caused solely by treating synchronization as a backup.
- Behavior caused by adding an untrusted device to a writable shared folder.

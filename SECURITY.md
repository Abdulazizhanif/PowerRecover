# Security Policy

PowerRecover works with disks, raw files, and recovery output. Please treat
security and privacy issues seriously.

## Reporting Security Issues

If you find a vulnerability, please do not open a public issue with exploit
details. Report it privately through GitHub Security Advisories after the
public repository is created.

## Privacy Rules

Do not upload:

- Real recovered user files.
- Personal documents, photos, invoices, or customer data.
- Disk images from real machines.
- Logs containing private file paths or device names.
- Passwords, recovery keys, API keys, or tokens.

## Scope

Security-sensitive areas include:

- Raw disk access.
- Recovery output path handling.
- Archive and Office file parsing.
- Encrypted volume helpers.
- Bootable USB startup behavior.

## Safety Model

PowerRecover should scan read-only and write recovered files only to a user
chosen output folder. Any change that writes to the source drive must be treated
as a critical bug.

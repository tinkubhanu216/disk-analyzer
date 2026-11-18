# Security Policy

## Supported versions

Security fixes are made for the latest release.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report it privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability**. Include the version, steps to reproduce, and the impact you
expect.

You should receive a response within a week. Once a fix is released, the report will be
credited (unless you prefer otherwise).

## Scope notes

Disk Analyzer is designed to be read-only. It never needs administrator rights to start.
When it runs elevated, it opens NTFS volumes with read access only to parse the Master File Table.
Any way to make the application write to, delete or alter user files, or to escalate
privileges, is in scope.

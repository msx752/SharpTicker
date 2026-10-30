# Security Policy

## Reporting a vulnerability

Please do **not** open a public issue for security problems.

Report it privately through GitHub instead: on the repository page, open **Security → Report a vulnerability** ([private vulnerability reporting](https://github.com/msx752/SharpTicker/security/advisories/new)). Include:

- a description of the vulnerability
- steps to reproduce
- the impact you expect

We will acknowledge the report as soon as possible and keep you updated while a fix is prepared.

## Supported versions

Security fixes are made on the latest released version. There is one version line: a fix ships as a new version of every package and covers all supported targets at once. Each package contains a separate build for the following frameworks:

| Target framework | Supported |
|------------------|-----------|
| .NET 10          | Yes       |
| .NET 9           | Yes, throughout 1.x |
| .NET 8           | Yes, throughout 1.x |

Microsoft ends support for .NET 8 and .NET 9 on November 10, 2026. SharpTicker fixes its own code for these targets for the whole 1.x line, but it cannot fix vulnerabilities in the .NET runtime itself; plan your .NET upgrade accordingly. See [Versioning](README.md#versioning).

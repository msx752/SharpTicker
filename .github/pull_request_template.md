## Summary

<!-- What does this change and why? -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change (public API, behavior or database schema)
- [ ] Refactoring (no behavior change)
- [ ] Documentation or samples

## Checklist

- [ ] `dotnet build SharpTicker.slnx -c Release` succeeds
- [ ] `dotnet test SharpTicker.slnx -c Release` passes on net8.0, net9.0 and net10.0 (each target is a separate build; one passing does not cover the others)
- [ ] Tests added or updated for the change
- [ ] Dashboard changes: `npm run build` and `npm test` pass in `src/SharpTicker.Dashboard/wwwroot`
- [ ] Storage changes: in-memory, EF Core, MongoDB and Redis providers behave the same
- [ ] Breaking changes follow [Versioning](https://github.com/msx752/SharpTicker#versioning): APIs are marked `[Obsolete]` first; schema changes only in a major version
- [ ] Documentation updated where needed (docs/, README, samples)
- [ ] The change is my own work or comes from a compatibly licensed source, noted above (see [CONTRIBUTING](https://github.com/msx752/SharpTicker/blob/main/CONTRIBUTING.md#guidelines))

## Notes for reviewers

<!-- Anything that needs special attention, follow-up work, or screenshots for UI changes. -->

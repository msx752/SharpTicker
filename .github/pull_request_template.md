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
- [ ] Storage changes: EF Core, MongoDB and in-memory providers behave the same
- [ ] Documentation updated where needed

## Notes for reviewers

<!-- Anything that needs special attention, follow-up work, or screenshots for UI changes. -->

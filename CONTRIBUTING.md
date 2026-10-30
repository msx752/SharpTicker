# Contributing to SharpTicker

Thanks for your interest in improving SharpTicker. This guide explains how to get a change merged.

## How to contribute

1. Open an issue first for anything larger than a small fix, so the approach can be agreed on.
2. Fork the repository and create a branch from `main`.
3. Make your change, with tests.
4. Make sure the build and all tests pass (see below).
5. Open a pull request against `main` and describe what changed and why.

## Development setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the exact band is pinned in `global.json`), plus the .NET 8 and .NET 9 runtimes to run the tests on every target framework
- [Node.js 20+](https://nodejs.org/), only when you work on the dashboard UI or build packages. Without it the libraries still build and test; the dashboard package just has no UI.

### Build

```bash
cd src/SharpTicker.Dashboard/wwwroot
npm ci
npm run build
cd ../../..

dotnet build SharpTicker.slnx -c Release
```

### Test

```bash
dotnet test SharpTicker.slnx -c Release
```

The test projects run on net8.0, net9.0 and net10.0. The dashboard UI has its own tests: `npm test` in `src/SharpTicker.Dashboard/wwwroot`.

### Working with multiple target frameworks

Every library targets net8.0, net9.0 and net10.0. We ship **one package with one version number**, and inside it three separate builds (`lib/net8.0`, `lib/net9.0`, `lib/net10.0`). NuGet gives each user only the build that matches their project.

The three builds come from the same source, but they are different binaries. Each one is compiled against its own dependency versions (`src/Directory.Build.props` pins Microsoft.Extensions and EF Core to the major version that matches each target), may contain different `#if` branches, and runs on its own runtime. **A green test run on one target says nothing about the others**, which is why CI tests every target in its own job.

A bug that shows up on one target usually comes from one of these:

- a different dependency version (EF Core query translation is the most common case),
- a runtime or base class library behavior change,
- a `#if NETx_0_OR_GREATER` branch in our own code.

**Reproduce on one target.** Pass `-f` to a test project, or to a sample:

```bash
dotnet test tests/SharpTicker.EntityFrameworkCore.Tests -c Release -f net8.0 --filter "FullyQualifiedName~CronTicker"
dotnet run --project samples/SharpTicker.Sample.WebApi -f net10.0
```

Use `-f` on a project, not on the solution: some projects (samples, the source generator) do not have every target. CI runs the same script per target, `bash .github/scripts/test.sh net8.0`.

**Debug on one target.** In Visual Studio, pick the framework in the debug target dropdown; Test Explorer lists each test once per framework. In Rider, set "Target framework" in the run configuration. From the command line, `VSTEST_HOST_DEBUG=1 dotnet test ... -f net8.0` waits for a debugger to attach.

**Fix it, in this order of preference:**

1. Use an API that behaves the same on every target.
2. Put the difference in one small compatibility file instead of scattering `#if` blocks (see `UpdateSettersBuilderCompat.cs`).
3. If a `#if` is unavoidable, use the `NETx_0_OR_GREATER` form, never an exact `NET8_0`. New targets then pick up the newest code path, and dropping an old target is a search for `!NETx_0_OR_GREATER`.
4. For large differences, use a separate file included only for that target with a `Condition` on `TargetFramework` in the project file.

Add a regression test that runs on all targets. Guard a test with `#if` only when the API under test does not exist on a target.

**Newest patches.** Version ranges such as `[8.0.0,9.0.0)` resolve to their lowest version, so regular builds test the oldest supported patches. A weekly workflow, `latest-patches.yml`, reruns the tests with every dependency floated to the newest release of its major version. Run it locally with:

```bash
bash .github/scripts/test.sh net8.0 -p:LatestPatchDependencies=true
```

## Guidelines

- Follow the existing code style and conventions.
- Add or update tests for every behavior change.
- Keep pull requests focused: one feature or fix per pull request.
- Write commit messages, comments, docs and issues in English.
- Do not add source files from other projects unless their license allows it and the origin is noted in the pull request.
- SharpTicker is developed independently of the project it was forked from (see [Origin](README.md#origin)). Do not port code, tests or documentation from that project's later releases; they are published under a different license. Contributions must be your own work or come from compatibly licensed sources.

## Reporting issues

Use [GitHub Issues](https://github.com/msx752/SharpTicker/issues) for bugs and feature requests. Include steps to reproduce, the expected behavior and the actual behavior. Report security problems privately as described in [SECURITY.md](SECURITY.md).

## Releasing

For maintainers. `main` is protected: changes land through pull requests once CI passes.

1. Create a GitHub release with a new tag `v<version>` on a commit of `main`, for example `v1.2.0` or `v1.2.0-rc.1`. The version follows [Semantic Versioning](https://semver.org) and is used for every package.
2. Tick **Set as a pre-release** exactly when the version has a `-` suffix. NuGet treats those versions as prereleases.
3. Publishing the release starts `release.yml`. It first checks the tag format, the pre-release flag, that the commit is on `main` and that the version is not on NuGet yet. Then it builds, tests on every target and packs all packages.
4. The `nuget` environment then waits for a reviewer to approve the publish. After approval, the publish job pushes the packages that were built in step 3. NuGet versions cannot be deleted, only unlisted, so try a pipeline change with a pre-release first.

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): no API key is stored in the repository. The policy on nuget.org allows `msx752/SharpTicker`, workflow `release.yml` and environment `nuget` to push packages matching `SharpTicker*`. Renaming the workflow file or the environment breaks publishing until the policy is updated to match.

If the push stops partway, re-run only the failed job; packages that were already pushed are skipped.

## License

By contributing, you agree that your contributions are licensed under the project's dual license, [MIT or Apache 2.0](LICENSE), at the user's option.

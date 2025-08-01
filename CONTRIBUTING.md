# Contributing

We love receiving PRs from the community with features and fixed.
For a big feature it's advised to raise an issue to discuss it first.

# Guidelines

* Please avoid mixing changes needed for a feature with other changes such as refactors, automated IDE changes like adding BOM characters, empty lines, etc.
* Feel free to start with a draft PR, while you work on it. You can ask pointed questions by reviewing your own code this way, while signaling to the reviewer that the PR isn't ready for review just yet.
* Mark the PR ready for review once you've completed the change, including:
  * The description should link to relevant context such as tickets, discussions or previous PRs. Consider screenshots of the events in Sentry or other relevant visual things.
  * Add tests that verify your change. The repo has lots of examples of Unit and integration tests. Including device tests that run on Android and iOS.
  * CI should be green.
  * The ideal state is where a reviewer approves and merges it immediately. But on more complex changes, some back and forth during reviews is expected.

## TLDR

* Install the .NET SDKs
* Install PowerShell
* Install Xcode
* Restore workloads with `dotnet workload restore` (needs `sudo` on a Mac)
* To quickly get up and running, you can just run `dotnet build SentryNoMobile.slnf` (you're skipping the mobile targets)
* To run a full build in Release mode and test, before pushing, run `./build.sh` or `./build.cmd`

## Minimal Dependencies

* The latest versions of the following .NET SDKs:
  - [.NET 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
  - [.NET 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

  *Technically, you only need the full SDK installation for the latest version (9.0). If you like, you can install the smaller ASP.NET Core Runtime packages for .NET 8.0. However, installing the full SDKs will also give you the runtimes.*

* [`pwsh`](https://github.com/PowerShell/PowerShell#get-powershell) Core version 6 or later on PATH.

* `CMake` on PATH. On Windows you can install the [C++ CMake tools for Windows](https://learn.microsoft.com/en-us/cpp/build/cmake-projects-in-visual-studio?view=msvc-170#installation). On macOS and Linux you can use your favourite package manager (e.g. `brew install cmake` or `apt install cmake`).

* On Windows:
  - [.NET Framework](https://dotnet.microsoft.com/download/dotnet-framework) 4.6.2 or higher.
  - `Sentry.DiagnosticSource.IntegrationTests.csproj` uses [SQL LocalDb](https://docs.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb) - [download SQL LocalDB 2019](https://download.microsoft.com/download/7/c/1/7c14e92e-bdcb-4f89-b7cf-93543e7112d1/SqlLocalDB.msi). To avoid running these tests, unload `Sentry.DiagnosticSource.IntegrationTests.csproj` from the solution.
* On macOS/Linux
  - [Mono 6 or higher](https://www.mono-project.com/download/stable) to run the unit tests on the `net4x` targets.

## .NET MAUI Requirements

To build any of `Sentry.Maui`, `Sentry.Maui.Tests`, or `Sentry.Samples.Maui`, you'll need to have the MAUI workloads installed. You can do so by running `dotnet workload restore` from the root of the SDK's repository (or `sudo dotnet workload restore` on macOS/Linux).

See https://docs.microsoft.com/dotnet/maui/ for details. JetBrains also have a great blog post if you're developing on a Mac: https://blog.jetbrains.com/dotnet/2022/05/25/macos-environment-setup-for-maui-development/

Basically, if you can build and run the `MyMauiApp` example you should also be able to build and run the Sentry MAUI sample app.

### Targeting Android, iOS and Mac Catalyst

* Targeting the mobile platforms requires aditional dependencies. 
  - `Java` is required for building the Android bindings. If you're building Sentry using an IDE you provide the path to your Java installation via the IDE settings (open the settings for Visual Studio or Rider and search for "android"). Building Sentry from the command line (using `dotnet build`) requires `JAVA_HOME` to be available on the environment.
  - Compiling for iOS and Mac Catalyst happens on macOS only.

Although the files in `/src/Sentry/Platforms/` are part of the `Sentry` project, they are [conditionally targeted](https://github.com/getsentry/sentry-dotnet/blob/b1bfe1efc04eb4c911a85f1cf4cd2e5a176d7c8a/src/Sentry/Sentry.csproj#L19-L21) when the platform is Android, iOS, or Mac Catalyst. We build for Android on all platforms.

```xml
<!-- Platform-specific props included here -->
  <Import Project="Platforms\Android\Sentry.Android.props" Condition="'$(TargetPlatformIdentifier)' == 'android'" />
  <Import Project="Platforms\Cocoa\Sentry.Cocoa.props" Condition="'$(TargetPlatformIdentifier)' == 'ios' Or '$(TargetPlatformIdentifier)' == 'maccatalyst'" />
```

These `*.props` files are used to add platform-specific files, such as references to the binding projects for each native SDK. These binding projects are .NET wrappers around native Android or Cocoa SDK functions.

Also note `/Directory.Build.targets` contains some [convention-based rules](https://github.com/getsentry/sentry-dotnet/blob/4e7496b45465c5561767cfd8f2914740cc3dfdf6/Directory.Build.targets#L20-L37) to exclude code that is not relevant for the target platform. Developers using Visual Studio will need to enable `Show All Files` in order to be able to see these files, when working with the solution.

## Legacy ASP.NET solutions

When debugging a legacy ASP.NET application with project references to `Sentry.AspNet`, you may need the following workarounds to tooling issues:

#### Microsoft.WebApplication.targets not found

* [Disable Microsoft.WebApplication.targets in Rider](https://youtrack.jetbrains.com/issue/RIDER-87113/Cannot-build-.NET-Framework-projects-with-legacy-style-csproj-after-upgrading-to-2022.3.1)

#### CodeTaskFactory not supported

* [Disable CodeTaskFactory in Roslyn](https://github.com/aspnet/RoslynCodeDomProvider/issues/51#issuecomment-396329427)

## Solution Filters

_TLDR;_ when working with the Sentry codebase, you should use the solution filters (not the solutions).

_Full explanation:_ 

The `Sentry.sln` solution contains all of the projects required to build Sentry, it's integrations and samples for every platform. However, the repository contains various solution filters that will be more practical for day-to-day tasks.

These solution filters get generated automatically by `/scripts/generate-solution-filters.ps1` so, although you can certainly create your own solution filters and manage these how you wish, don't try to modify any of the `*.slnf` files that are committed to source control. Instead, changes to these can be made by modifying `/scripts/generate-solution-filters-config.yml` and re-running the script that generates these.

Also note that script generates a `.generated.NoMobile.sln` solution, which is an identical copy of `Sentry.sln`. Again, we don't recommend opening this directly. It exists as a round about way to conditionally set build properties based on the solution name in certain solution filters. You should instead use those solution filters (e.g. `SentryNoMobile.slnf`) when working in the Sentry codebase.

## API changes approval process

This repository uses [Verify](https://github.com/VerifyTests/Verify) to store the public API diffs in snapshot files. When a change involves modifying the public API area (by for example adding a public method), that change will need to be approved, otherwise the CI process will fail.

To do that, run the build locally (i.e: `./build.sh` or `build.cmd`) and commit the `verify` files that were changed.

## Changelog

We'd love for users to update the SDK everytime and as soon as we make a new release. But in reality most users rarely update the SDK.
To help users see value in updating the SDK, we maintain a changelog file with entries split between two headings:

1. `### Features`
2. `### Fixes`

We add the heading in the first PR that's adding either a feature or fixes in the current release.
After a release, the [changelog file will contain only the last release entries](https://github.com/getsentry/sentry-dotnet/blob/main/CHANGELOG.md).

When you open a PR in such cases, you need to add a heading 2 named `## Unreleased`, which is replaced during release with the version number chosen.
Below that, you'll add heading 3 mentioned above. For example, if you're adding a feature "Attach screenshots when capturing errors on WPF", right after a release, you'd add to the changelog:

```
## Unreleased

### Features

- Attach screenshots when capturing errors on WPF (#PR number)
```

There's a GitHub action check to verify if an entry was added. If the entry isn't a user-facing change, you can skip the verification with `#skip-changelog` written to the PR description. The bot writes a comment in the PR with a suggestion entry to the changelog based on the PR title.

## Naming tests

Ideally we like tests to be named following the convention `Method_Context_Expectation`.

[For example](https://github.com/getsentry/sentry-dotnet/blob/ebd70ffafd5f8bd5eb6bb9ee1a03cac77ae67b8d/test/Sentry.Tests/HubTests.cs#L43C1-L44C68):
```csharp
    [Fact]
    public void PushScope_BreadcrumbWithinScope_NotVisibleOutside()
```

## Verify tests

Some tests use [Verify](https://github.com/VerifyTests/Verify) to check returned objects against snapshots that are part of the repo.
In case you're making code changes that produce many (intended) changes in those snapshots, you can use [accept-verifier-changes.ps1](./scripts/accept-verifier-changes.ps1) like this:

```shell-script
dotnet test
pwsh ./scripts/accept-verifier-changes.ps1
```

You may need to run this multiple times because `dotnet test` stops after a certain number of failures.

## Integration tests

Directory [./integration-test](./integration-test/) contains [Pester](https://pester.dev/)-based integration tests.
These tests create sample apps with `dotnet new` and run against local nuget packages (.nuget files).
In CI, these packages are expected to be present, while locally, scripts will run `nuget pack` automatically.

### Running integration tests locally

You can run individual tests either via Pester integration (e.g. in VS Code), or from command line: `./integration-test/cli.Tests.ps1`. Consult Pester docs for details on how to write tests.

Because these tests rely on a Sentry server mock (`Invoke-SentryServer`) from <https://github.com/getsentry/github-workflows/tree/main/sentry-cli/integration-test>, you need to check out [getsentry/github-workflows](https://github.com/getsentry/github-workflows) as a sibling directory next to your `getsentry/sentry-dotnet` checkout.

## Maintaining the Ben.Demystifier Submodule

This repo uses a variety of techniques to vendor in third party code without creating external dependencies. One of
those is submodules.

One of those submodules is Ben.Demystifier, which was originally written by Ben Adams. Attempts to contact Ben in recent 
years have been unsuccessful, so we've started maintaining a permanent fork of the project at:
- https://github.com/getsentry/Ben.Demystifier

Any significant changes to the submodule should be made in a branch and merged into the submodule's `main` branch. 
However, many of the Ben.Demystifier members are public. That makes sense if people are using Ben.Demystifier as a 
library, but in this repo we want to keep those members internal.

Once changes to Ben.Demystifier have been merged into the main branch then, the `internal` branch of Ben.Demystifier 
should be updated from the main branch and the `modules/make-internal.sh` script run again (if necessary). This repo 
should reference the most recent commit on the `internal` branch of Ben.Demystifier then (functionally identical to the
main branch - the only difference being the changes to member visibility).

# Portalkeeper Development Guide

## Project Purpose

Portalkeeper is a cross-platform launcher and client-management tool for World of Warcraft 3.3.5a private realms.

Its responsibilities include:

* validating an existing WoW 3.3.5a client installation
* managing realm configuration
* managing supported custom addons
* applying realm-specific client settings
* launching the client
* supporting realm-specific requirements such as addons and patch files

Portalkeeper does not distribute the World of Warcraft client.

## Supported Platforms

Portalkeeper must remain functional on:

* Windows x64
* Linux x64

Do not solve a platform-specific problem by breaking behavior on the other supported platform.

When introducing native libraries or platform-specific behavior:

* isolate platform-specific code where practical
* preserve existing behavior on unaffected platforms
* verify runtime and publish paths separately from compile-time behavior
* do not assume Windows filesystem, path, executable, or library-loading behavior applies to Linux
* do not assume Linux behavior applies to Windows

## Technology

Portalkeeper uses:

* .NET
* Avalonia UI

Prefer existing project patterns and APIs before introducing new abstractions or dependencies.

Do not introduce Visual Studio-specific project requirements.

The project should remain buildable and maintainable using standard .NET CLI tooling.

## Architecture Principles

### Realm Configuration

Realm-specific behavior belongs in realm configuration when practical.

Examples include:

* realm address
* realm-specific addon requirements
* realm-specific client patch requirements
* other settings required to connect correctly to a particular realm

Do not duplicate realm-specific state in global application configuration unless compatibility or migration requires it.

### Global Configuration

Application-wide preferences that are not specific to one realm belong in Portalkeeper's global configuration.

Keep the distinction between global application settings and realm-specific requirements clear.

### Existing Behavior

Preserve working behavior outside the requested task.

Do not casually change:

* configuration formats
* configuration defaults
* file locations
* addon installation behavior
* client validation behavior
* launch behavior
* native dependency handling
* backup behavior
* Linux behavior while fixing Windows
* Windows behavior while fixing Linux

If a requested feature requires one of these behaviors to change, make that change explicit and keep it narrowly scoped.

## Client Handling

Portalkeeper works with a user's existing World of Warcraft 3.3.5a installation.

Do not add functionality that assumes Portalkeeper distributes Blizzard client files.

Client-related functionality may inspect or manage files in the user's existing client installation when required for Portalkeeper features.

Be conservative when modifying client files.

When replacing or modifying existing files:

* preserve recoverability where appropriate
* avoid destructive behavior when a safer approach exists
* do not overwrite unrelated client data
* verify paths before performing file operations

## WoW 3.3.5a Validation

Portalkeeper validates that the selected client is appropriate for WoW 3.3.5a.

Existing validation behavior should be reused rather than duplicated.

Do not weaken validation merely to make a new feature easier to implement.

If validation must change, inspect the current validator and understand its assumptions first.

## Realm Configuration Files

Realm configuration files are intended to become the authoritative source for realm-specific requirements.

When adding new realm configuration capabilities:

* extend the existing parser/model pattern
* preserve compatibility when required by the task
* validate malformed or missing values appropriately
* avoid scattering realm parsing logic across unrelated UI code
* keep parsing, runtime behavior, and UI responsibilities appropriately separated

Do not redesign the entire configuration system while implementing one configuration field or section.

## Addon Management

Portalkeeper manages selected custom addons used by supported realms.

Existing functionality includes operations such as:

* install
* update
* update all
* remove

Addon definitions may include repository or source information used by Portalkeeper to manage the addon.

When modifying addon management:

* preserve existing working install/update/remove behavior unless explicitly changing it
* distinguish addon metadata from installed addon state
* avoid embedding realm-specific addon requirements in global code when they belong in realm configuration
* keep filesystem operations safe and predictable
* validate paths before deleting or replacing addon directories

Do not broaden an addon-management change into an unrelated redesign of the launcher.

## Realm Patch Requirements

Portalkeeper may support realm-specific client patch requirements.

The intended direction is for a realm configuration to describe which client patch files that realm requires.

When this functionality is implemented, Portalkeeper should be able to determine whether the selected client satisfies those requirements before connecting to the realm.

Do not assume patch files are identical across realms.

Do not distribute Blizzard-owned assets as part of Portalkeeper.

Prefer validation and management of files already legitimately available to the user or supplied separately by the realm administrator.

## Native Libraries

Some functionality may require native dependencies such as StormLib.

When working with native libraries:

* account for Windows and Linux separately
* verify the native library is included in the correct publish output
* verify runtime loading searches the location where the library is actually deployed
* do not treat successful compilation as proof that native runtime loading works
* preserve unaffected platform behavior

## UI Changes

Portalkeeper uses Avalonia.

When modifying UI:

* follow existing visual and layout patterns
* avoid unnecessary redesign
* preserve existing functionality outside the requested change
* keep business logic out of UI code when an existing service/model layer is appropriate
* consider window resizing and normal layout behavior
* consider both Windows and Linux behavior

Do not introduce a new UI framework or unrelated styling system.

## Dependencies

Do not add a dependency merely because it makes a small task easier.

Before adding a dependency:

1. Check whether the project already has equivalent functionality.
2. Check whether the .NET runtime or existing dependencies provide what is needed.
3. Confirm the dependency supports Windows and Linux if the affected feature is cross-platform.
4. Ensure the dependency is necessary for the requested task.

Document why a new dependency is required.

## Scope Discipline

Treat each requested task as narrowly scoped.

Before editing:

* identify the requested behavior
* locate the relevant implementation
* inspect surrounding dependencies
* identify existing patterns to follow

Do not perform unrelated:

* refactoring
* renaming
* formatting
* cleanup
* modernization
* dependency updates
* architecture changes
* documentation rewrites

If an unrelated problem is discovered, report it separately rather than fixing it automatically.

## Debugging

Do not guess at fixes.

For bugs or failures:

1. Reproduce or inspect the failure.
2. Read the complete relevant error output.
3. Trace the relevant code path.
4. Find similar working behavior where useful.
5. Form a specific root-cause hypothesis.
6. Test the smallest practical change.
7. Implement the fix only after evidence supports the cause.

Do not stack speculative fixes.

If repeated fixes fail, stop and reconsider the assumptions or architecture.

## Verification

Implementation is not completion.

For code changes, use the appropriate verification available for the task.

At minimum, when applicable:

```bash
dotnet build
```

Also use relevant tests when they exist.

For packaging or publishing changes, create and inspect the actual publish/package output.

For native dependencies, verify the expected native files are present in the produced output.

For runtime-sensitive changes, distinguish between:

* compiled successfully
* verified by inspection
* actually run and tested

Do not claim testing occurred on a platform that was not actually tested.

## Git Review

Before reporting a coding task complete, inspect:

```bash
git status --short
git diff
```

Review all changed and untracked files.

Every intentional source change should be necessary for the requested task.

Generated build, publish, cache, temporary, and diagnostic artifacts should not become source changes unless explicitly required as deliverables.

The `dist/` directory is not source code and should remain excluded from normal repository changes.

## Completion Report

After completing an implementation task, report:

* files intentionally changed
* what behavior changed
* build or test commands actually run
* results of those commands
* anything verified only through inspection
* anything that still requires platform-specific or manual testing
* unrelated issues discovered but intentionally left unchanged

Do not say a feature is complete merely because the code was written.

## Working Style for OpenCode

Prefer small, bounded tasks.

A good task changes one coherent behavior or introduces one clearly defined implementation step.

Avoid attempting an entire multi-stage feature in one implementation pass.

For larger work:

1. inspect the existing implementation
2. divide the feature into independently understandable steps
3. implement one step
4. verify it
5. review the result
6. continue with the next step

A clean OpenCode session is preferred when beginning a substantially different task.

Use existing project context and code as the source of truth rather than inventing architecture that has not been requested.

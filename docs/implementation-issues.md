# Implementation issues found during documentation review

These are source-review findings, not changes made by this documentation update.

- `StatusURL` is parsed into realm configuration but has no consumer. Health checks use TCP authentication/world ports. Do not advertise an HTTP status integration yet.
- News/calendar cache filenames are shared across realm URLs. After changing realm configuration, an offline load can show the previous realm's cached feed. Armory JSON caches are URL-scoped.
- The Linux package is self-contained for .NET but the project/script do not bundle StormLib. Windows native dependency distribution remains undefined/unverified.
- The publishing script copies README/LICENSE and project-configured files, but not `docs/`. README's repository-relative documentation/source links therefore need a source checkout and may not resolve inside the generated runtime ZIP. Packaging changes are deferred to a code/package task.
- Historical reports reference `tools/ArmoryClientProbe`, `tests/ArmoryTransmogChecks` and `tests/ArmoryMaterialChecks`, which are absent from this actual checkout. Preserve/recover these from prior artifacts if repeatable developer checks are needed; this review did not delete tools or recreate unverified harnesses.
- Custom archive handling is intentionally limited: unknown `patch*` names and encountered delta/deletion records fail to fallback; unrelated non-stock archive names are not part of the fixed priority list. This is not general custom-patch compatibility.
- Archive cache fingerprints use metadata, not file-content hashes. Changing bytes while preserving size/time can evade automatic invalidation.

Expected scope limitations: static pose, approximate portrait lighting, no animation/particles, omitted layered reflection effects and missing custom guild-tabard artwork. See [preview behavior](armory-0.6-integration.md) and [verification gaps](verification.md). No feature, application-code, release-version, commit or push changes were made for this review.

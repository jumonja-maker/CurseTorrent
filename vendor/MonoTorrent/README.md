# MonoTorrent source dependency

Upstream: https://github.com/alanmcgovern/monotorrent
Base release: 3.0.2, commit `e78faebd0aec117146cffccaaea987ab0629eec0`.
License: MIT; the original license and file copyright notices are retained.

This directory contains production source and shared helpers only, built as the original eleven assemblies for .NET 10. Sources are compiled directly; the build does not fetch a moving Git branch or download an unofficial binary. Mono.Nat 3.0.0 and ReusableTasks 4.0.0 remain pinned NuGet dependencies. Each project has a checked-in lock file.

## Local changes

- Build: .NET 10 target, fixed provenance/version, compile-transitive project references, no GitInfo build dependency, current UDP overload adapter, obsolete serialization constructors explicitly marked.
- Disk IO: hash reads open existing files without truncation or creation; failed opens release their stream locks; zero files go through IPieceWriter and honor DoNotDownload. CurseTorrent adds ownership/consent protection in its writer.
- Lifecycle: bounded cleanup of metadata-only managers, start/hash/stop ownership guards, stable cancellation tokens, read-only hash verification and controlled completion renames; a declined move preserves the real logical path; upload counters captured before stop/reset.
- Metadata: a 16 MiB advertised size limit before allocation and integer conversion.
- Bencoding: shared depth/object/byte budgets in both decoders, checked numeric and string-length ranges, complete handling of partial stream reads.

Production fixes are covered by the application's original integration suite and the regression, file safety, compatibility and Windows UI projects in `tests/`. The reference audit describes the baseline before these changes; the repair report records validation of this build.

When updating upstream, review these changes explicitly and rerun all regression suites. Do not replace this directory with the unpatched 3.0.2 NuGet package.

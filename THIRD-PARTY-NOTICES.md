# Third-party notices

CurseTorrent's own code is licensed under the [MIT License](LICENSE).
The Windows distribution also contains the following third-party software.

| Component | Version | License | Source | Full license text |
| --- | --- | --- | --- | --- |
| MonoTorrent | 3.0.2, CurseTorrent source patch 1 | MIT | https://github.com/alanmcgovern/monotorrent/tree/release-v3.0.2 | [MonoTorrent](licenses/MonoTorrent-3.0.2.txt) |
| Mono.Nat | 3.0.0 | MIT | https://github.com/mono/Mono.Nat | [Mono.Nat](licenses/Mono.Nat-3.0.0.txt) |
| ReusableTasks | 4.0.0 | MIT | https://github.com/alanmcgovern/ReusableTasks | [ReusableTasks](licenses/ReusableTasks-4.0.0.txt) |
| Microsoft .NET and Windows Desktop runtime | Version selected by the .NET 10 SDK at publication | MIT and component-specific notices | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf | Runtime license and notices copied to `licenses/runtime/` during packaging |

The first three license files above were copied from the exact released NuGet packages.
MonoTorrent is compiled from its pinned, locally patched source at commit
`e78faebd0aec117146cffccaaea987ab0629eec0` for .NET 10. Its provenance and patch scope
are documented in `vendor/MonoTorrent/README.md`. Mono.Nat and ReusableTasks retain
their exact resolved NuGet versions and hashes in the checked-in lock files.

Self-contained Windows packages include .NET runtime files so that the user does not
need to install .NET separately. `scripts/build.ps1` collects license and notice files
from the resolved Microsoft runtime packs rather than assuming a different runtime version.

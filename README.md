# EFF Mod Manager

Windows application for installing EFF, managing local Mods, and repairing older Mod packages. This repository keeps its own source history; releases use verified, locally deployed artifacts from the EFF main checkout.

The unified EFF architecture, shared compiler package and user-selected index paths are documented in the [EFF main repository](https://github.com/ssice-a/EFF), especially [Shared Tools and Indexes](https://github.com/ssice-a/EFF/blob/main/docs/shared-tools-and-indexes.md).

Build and test with the .NET 9 Windows SDK from the EFF checkout. Manager links the pinned AnimeStudio core and index builder at build time and ships the resulting reader and decompression library; users do not need to install AnimeStudio or Blender. The EFF main repository's `tools/AnimeStudio` gitlink fixes this build-time dependency. A standalone Manager checkout without that source and the native validator is not a complete build environment.

- A manifest's author ID describes reusable content. Installing another copy creates a distinct directory and leaves earlier installations intact. Migration from backup does the same; migrating selected installed Format 2 Mods replaces each selected directory transactionally and keeps rollback copies.
- Format 3 checking uses the shared native manifest/control compiler shipped in `native/eff_author_assets_format3.dll`. It validates INI relationships, generic control expressions, Part visibility, and the existence and containment of declared files. It does not open payloads, build `compiled.bin`, download a compiler or require a game index. Native resource contents, legacy control resources, sampled animation data, physics/effect content and current game logical matches are validated by the demand-driven runtime; a successful declaration check is not full in-game acceptance.
- Missing authored files or invalid declarations are reported precisely and left unchanged. Manager cannot reconstruct missing geometry/pixels or guess a new logical target. Restore the missing resource or re-export it from the author project. Valid Format 3 packages require no offline cache repair.
- Format 2 migration uses one index-acquisition policy: a validated selected/registered workspace, a manually supplied or already cached portable `.effindex.gz`, an exact matching online index, then a bundled local VFS/Bundle index build. Network failure or an unpublished game version does not prevent local index construction. Migration still needs its compatible migration toolset; prepare it in Settings or install a complete verified shared-tools package before working offline. The user selects the resource/index location.

From the EFF root, build the native validator before building Manager:

```powershell
cmd /d /c tools\diagnostics\build_author_assets.cmd format3
dotnet run --project EFF-Mod-Manager/tests/EFF.ModManager.Tests/EFF.ModManager.Tests.csproj -c Release
dotnet run --project EFF-Mod-Manager/tests/EFF.ModManager.IntegrationTests/EFF.ModManager.IntegrationTests.csproj -c Release
```

The integration suite calls the actual Repair service, parses a small real UnityFS/SerializedFile fixture through the local VFS reader, exercises offline and manual index acquisition, and checks that linked core dependency versions stay in sync. Local installation remains the responsibility of `tools/release/build_deploy_local.ps1 -Component Manager` in the EFF main repository.

Release packages must come from the locally built, tested and deployed Manager directory (`E:\EFF-Mod-Manager`) under the EFF main repository's `docs/release-management.md` procedure. Include the complete publish output: the Manager executable, `native/eff_author_assets_format3.dll`, `AnimeStudio.Ooz.dll`, `CNKeys.json`, native texture libraries and notices/licenses. Do not copy only the executable. The manual GitHub workflow intentionally reports this policy instead of rebuilding source or creating a different release artifact in the cloud.

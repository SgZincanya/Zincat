# Zincat

VRChat Avatar tools for Unity 2022.3. Full distribution, including dance animations and audio.

## Install / 更新

Add this repository listing to VCC / ALCOM:

`https://raw.githubusercontent.com/SgZincanya/Zincat/main/docs/index.json`

Then choose **Zincat** in Manage Project. Updates appear after a new version is released; pushing source alone does not publish a version.

Required: VRChat Avatars SDK 3.10.4+, Modular Avatar 1.18.7+, NDMF 1.14.8+. Add the [nadena VPM repository](https://vpm.nadena.dev/vpm.json) if it is not already configured.

Optional integrations: CAU for queued uploading; Poiyomi/ThryEditor for Poiyomi materials; Gesture Manager for simulation; Starmesh/HaiMeshLib for mesh matching. Included dance material requires lilToon. These third-party plugins are installed separately.

## Existing Assets installation

Back up the project before the first VPM migration. The manifest migrates individual shipped files while retaining existing user data folders. Script and asset GUIDs are preserved. Package code lives in `Packages/io.github.sgzincanya.zincat`; writable user data continues to live under `Assets/Zincat` or Library/ProjectSettings/UserSettings as before. Existing shader preparation may modify materials according to the tool's settings.

Excluded from all distributions:

- `Clothing2.0/Cache` (generated wardrobe icons)
- `AvatarSplitTest/UploadSettings` (personal CAU settings)
- `Zincat兼容/UploadSettings` (personal cover photos)

The four shared menu icons and all functional source files are included. Do not delete the retained user folders to update the tool.

## Development / 发布

Install Git LFS before cloning, then run `git lfs pull` if animation files appear as pointer text. The repository keeps all animation and audio assets together.

Package: `Packages/io.github.sgzincanya.zincat`. Build a deterministic release ZIP and listing with `python tools/build_release.py`. Set a new three-part version in package.json, update its release URL, build and commit the listing, then push the matching `vX.Y.Z` tag. The publish workflow uploads the ZIP as a GitHub Release. Keep previous releases available so existing projects can restore their pinned versions.

First VPM release: **2.11.0**. Existing direct `.unitypackage` releases are not automatically managed by VCC until migrated.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for licensing scope.

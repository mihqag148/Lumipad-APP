# Repository split audit

## Source and verification

- Original repository: `mihqag148/MacroPad`, renamed to [RYNOR-ONE](https://github.com/mihqag148/RYNOR-ONE).
- Source commit: `fd04bc68bd063b9c15b7db0b89902a97b0799e9d`.
- Exact import commit in this repository: `150b04d`.
- Both source and import have the identical `pc-app` Git tree: `ff9e4e2650c0e5c121ace0d120a344fa959c63b5` (including binary logo).
- App adaptation commit: `77cf2f3`; RYNOR ONE split: `0494412`; PIXEL PRO documentation/build/reference: `1a641ea`, `901cba4`.
- PIXEL PRO source `f340ac76cef75c302e7ff68cf4d6186f3481949c` contains no desktop app project. Its VIA JSON matches the packaged app JSON byte-for-byte.

## All 23 copied app files

Paths remain unchanged on main. SHA values below identify the original imported contents before branding/updater changes.

| Source and destination path | Original Git blob |
|---|---|
| `pc-app/LumiPad.App/ActionKeymapWindow.cs` | `81053ba003bebe16a6fb58d3ec9cfe125b053737` |
| `pc-app/LumiPad.App/ActionScriptEngine.cs` | `eac56861448566b1bb3706826d9a1e1a169ca977` |
| `pc-app/LumiPad.App/App.xaml` | `ccc79cc67b98fcb2519af17a9f749ddcfab445ea` |
| `pc-app/LumiPad.App/App.xaml.cs` | `70b163007718037285c10cad41950f5de453572a` |
| `pc-app/LumiPad.App/Assets/LumiPadLogo.png` | `67f03a606652ddc0ad174a9d239583aefffd36c8` |
| `pc-app/LumiPad.App/AutoProfileService.cs` | `d3b55646a29b79c60980763e221c3e9245f9880e` |
| `pc-app/LumiPad.App/ComboBoxSelectionTextConverter.cs` | `dae32f1f82b22982b4be101265164318a9f7984d` |
| `pc-app/LumiPad.App/Definitions/PIXEL-PRO-VIA.json` | `3f7836bd963ab27eebb08dd5ead52b01d3df54e4` |
| `pc-app/LumiPad.App/DeviceLink.cs` | `43dba5341b715b3f8c7e662d65c0f81fbfc752e0` |
| `pc-app/LumiPad.App/DiagnosticLogWindow.cs` | `586af8f461d4e21846bfa9a55e0faaf02d57db35` |
| `pc-app/LumiPad.App/LumiPad.App.csproj` | `82279f339abf6efe3f71c89732cc63061ed9a2ea` |
| `pc-app/LumiPad.App/MainWindow.xaml` | `bb5b93eea0181552bfce7e8f8139a662d1edd8ca` |
| `pc-app/LumiPad.App/MainWindow.xaml.cs` | `2b4629a3d1d7aa5615052e8a931acb6620d65472` |
| `pc-app/LumiPad.App/NowPlayingService.cs` | `1c30da1af2ca79b5b60e6f6a29ebc371f867a844` |
| `pc-app/LumiPad.App/PcMonitorService.cs` | `e78f93884d6eec70f66bea8bfaab629596b71e4e` |
| `pc-app/LumiPad.App/ProductCatalog.cs` | `ed7b894c8a3ca9067a30a609cd947b4e7c699405` |
| `pc-app/LumiPad.App/QmkRawHidLink.cs` | `6e593db7681f657dbac000898adc1522d05043df` |
| `pc-app/LumiPad.App/ScreensaverMediaService.cs` | `eae17096bf220c34f60c0fbd60ff0f8cd9c64bb9` |
| `pc-app/LumiPad.App/SerialLink.cs` | `1500cce32cca2ac6f643b45c25871d01a0af2d03` |
| `pc-app/LumiPad.App/SystemVolumeService.cs` | `b14d314898bfec42fb21ee2f05ee82adb59f782e` |
| `pc-app/LumiPad.App/TextBitmapRenderer.cs` | `6568e21bc490d923f5002799ca5ee0cb79c7d039` |
| `pc-app/LumiPad.App/app.manifest` | `66dec9c4c48b04136046e94a10a6aed41ab0046b` |
| `pc-app/README.md` | `31445b1c67711f2c960719fe94a804d1440f215a` |

## Build and documentation ownership

- Original `build-app.yml` and `windows-app.yml` are consolidated into this repository's `.github/workflows/windows-app.yml`: .NET 8, self-contained win-x64 publish, artifact, then app-only release.
- Original mixed `publish-latest.yml` is replaced by two release jobs with explicit build dependencies. No polling for unrelated workflow commits remains.
- RYNOR ONE `build.yml` uses its own `VERSION` file, includes `dts/**` in build triggers, and produces only UF2/firmware manifest. Original placeholder `blank.yml` remains; it is not a firmware build check.
- App/protocol release notes from the original root README are preserved in `docs/pre-split-history.md`. Historical paths there refer to the original snapshot.
- PIXEL PRO's README now describes the actual ESP-IDF build. Pull requests build but cannot execute its release step. Arduino bring-up code, ESP-IDF sources and VIA definition remain firmware-owned.
- Three `qmk/lumi_raw_hid/` files are copied to PIXEL PRO as firmware reference material and also retained in RYNOR ONE; they are not desktop app source and are not compiled by either current firmware workflow.
- RYNOR ONE USB/BLE product names, splash, shield display labels and current README use RYNOR ONE. Shield identifiers, pins, protocols and app storage ID `dial-desk` remain compatible.

## Branches and recovery

All original repository history and old releases remain intact. No force push or history rewrite was used.

The non-main branch `fix/screensaver-tearing-vdb` contains five commits not on the original main. Its app-only history was extracted into [archive/screensaver-tearing-vdb](https://github.com/mihqag148/Lumipad-APP/tree/archive/screensaver-tearing-vdb), tip `7a931a295920e9cc9cd20b26d1fec67ef591f696`. That archive has `LumiPad.App/` at its root because it is a subtree extraction; it is not the current app release branch. The original `fix/st7789-native-async-scan` has no commits beyond main. Original firmware branches were retained.

## Validation

- Local Windows self-contained publish succeeded (.NET SDK 8.0.425).
- Existing CS4014 warning in `MainTabs_SelectionChanged` remains: the dispatcher call is not awaited. This warning existed before the split and is not a build failure.
- Updater checks using the actual extracted production methods passed: independent RYNOR version, PIXEL manifest routing, app available despite firmware 404, firmware available despite app 404, and own-tag fallback when no manifest exists.
- UF2 asset lookup explicitly uses RYNOR-ONE; the flash destination remains `firmware.uf2`.
- CMake's new VERSION parsing passed a local CMake execution.
- Hardware USB/BLE/flash behavior still requires the physical devices; no physical flashing was performed.

Users of the old app should install the first standalone release from this repository once to switch the app update channel.


# Pre-split app and protocol history

Historical notes imported from RYNOR-ONE commit fd04bc68bd063b9c15b7db0b89902a97b0799e9d. Paths, product names and the former unified release process below describe that snapshot; see the root README for current builds and release ownership.

## Lumi Macropad Product Hub

Ứng dụng desktop hiện có tên **Lumi Macropad** và được tổ chức theo kiến trúc
multi-product. Khi mở app, Product Hub xuất hiện trước. Sản phẩm hiện tại là
**DIAL DESK**; card thiết bị hiển thị kết nối USB/Bluetooth và phần trăm pin thật
từ ZMK thông qua command `BAT`.

Danh sách sản phẩm nằm trong `pc-app/LumiPad.App/ProductCatalog.cs`, nên các
sản phẩm Lumi tiếp theo có thể thêm vào Product Hub mà không phải thay cấu trúc
giao diện điều khiển DIAL DESK.

Firmware DIAL DESK hiển thị splash **LUMI3D / DIAL DESK** cùng thanh loading
khoảng 2 giây khi màn hình khởi động, sau đó mới chuyển sang giao diện phím.


### PC Monitor v1.6

PC Monitor hỗ trợ nhiều GPU: **Auto** tự chọn GPU có tải realtime cao nhất (ưu tiên GPU rời NVIDIA/AMD khi tải bằng nhau), hoặc người dùng chọn thủ công từng GPU theo đúng tên model. App lưu lựa chọn GPU và tên cấu hình PC.

Screensaver có thể chọn **GIF / Image** hoặc **PC Monitor**. Khi chọn PC Monitor, DIAL DESK hiển thị telemetry realtime khi hết thời gian chờ thay vì chạy GIF. Tab PC Monitor nằm ngay sau Action.


### PC Monitor v1.7

PC Monitor cho phép tùy biến 6 vị trí hiển thị trên DIAL DESK (3 card lớn + 3 footer) với các chỉ số: CPU usage/temperature/clock, GPU usage/temperature/clock, RAM usage/used/total, network download/upload và FPS. Cấu hình được lưu trong app và gửi lại qua USB/Bluetooth khi kết nối.

Giao diện ComboBox dùng theme động hoàn toàn để light/dark đều đọc rõ, selection box luôn hiển thị đúng mục vừa chọn. Screensaver Source nằm ở góc phải của tiêu đề Screensaver. Tab đang chọn có viền cam kín bốn cạnh.

Final verified package: Lumi Macropad v1.7.1.


### PC Monitor v1.7.5

- ComboBox dùng template riêng với converter đọc trực tiếp SelectedItem nên text lựa chọn luôn cập nhật và màu dark/light không phụ thuộc theme mặc định của Windows.
- App yêu cầu quyền Administrator để LibreHardwareMonitor có thể đọc CPU/GPU temperature qua driver phần cứng.
- CPU temperature có thêm fallback qua LibreHardwareMonitor/OpenHardwareMonitor WMI và ACPI.
- 6 metric layout được nhúng vào mọi gói PCMON realtime, nên DIAL DESK luôn cập nhật layout kể cả khi gói PCCFG riêng bị trễ/mất.


### Multi-firmware app architecture v1.8.0

Lumi Macropad no longer makes the main UI depend directly on the DIAL DESK
`SerialLink` implementation. The app now has separate contracts for connection
and product commands:

- `IDeviceTransport`: USB/Bluetooth connection lifecycle.
- `IDeviceProtocol`: Media, RGB, screensaver, Action, PC Monitor, diagnostics,
  battery and device-control commands.
- `IDeviceLink`: combines transport and protocol for the UI.
- `DeviceLinkFactory`: selects a driver from the product definition.
- `DeviceDriverKind`: currently defines `LumiZmk`, `QmkRawHid` and
  `Esp32Companion`.

DIAL DESK still uses the existing `SerialLink` and the same Lumi protocol, so
this refactor does not change its USB CDC/BLE UUIDs or current features.
A future QMK product can add a Raw HID implementation of `IDeviceLink` and a
ProductCatalog entry without rewriting MainWindow or the shared app features.


### QMK Raw HID support v1.9.0

The Windows app now includes a real QMK Raw HID driver. QMK products use the
standard QMK Raw HID interface and the Lumi framing protocol defined in
`qmk/lumi_raw_hid`.

- QMK Raw HID uses the default QMK Usage Page/Usage ID `0xFF60 / 0x61`.
- Each QMK product should define its USB VID/PID in `ProductCatalog`.
- `QmkRawHidLink` probes only the configured VID/PID, requires a 32-byte QMK
  raw report, and verifies the device with a `HELLO` handshake before accepting it.
- Lumi messages are fragmented across fixed 32-byte QMK reports, so the shared
  app protocol can still carry Media, RGB, Profile, PC Monitor, Action and
  other product commands.
- Product selection now swaps the underlying device driver. Adding a QMK
  product to `ProductCatalog.All` no longer requires editing `MainWindow`.

To add a QMK product:

```csharp
public static ProductDefinition MyQmkPad { get; } =
    CreateQmkProduct(
        "my-qmk-pad",
        "MY QMK PAD",
        "QMK macro controller",
        "QP-01",
        0x1234,
        0x5678);
```

Then add `MyQmkPad` to `ProductCatalog.All`, copy the
`qmk/lumi_raw_hid` files into the QMK keymap/userspace, enable
`RAW_ENABLE = yes`, and implement the product command callback.


### Dynamic ZMK Studio / VIA tab v1.10.0

The embedded firmware-configuration tab now follows the selected product:

- ZMK products show **ZMK Studio** and load `https://zmk.studio/`.
- QMK Raw HID products show **VIA** and load `https://usevia.app/`.
- Other driver types fall back to a generic **Device Config** label.

For VIA, the app probes `navigator.hid` after the embedded web app loads.
When WebHID is available, the status reports **WebHID ready**. When the current
WebView2 runtime does not expose WebHID, the status tells the user to use the
existing **Open in Edge** button, which opens the same official VIA web app in
the browser.

The tab URL is reset whenever the active product changes, so switching between
a ZMK product and a QMK product also switches the embedded configurator.


### TFT backlight BLK / P0.08

Firmware reserves **P0.08** for the ST7789 module **BLK/backlight** control pin.

- ST7789 `BLK` -> nice!nano `P0.08`.
- TFT `GND` stays connected directly to nice!nano `GND`.
- Firmware holds P0.08 LOW during Zephyr startup.
- Backlight stays OFF while the ST7789/LVGL boot screen is initialized, then
  turns ON after a short delay.
- Soft sleep sends ST7789 DISPOFF and turns the backlight OFF.
- Wake sends DISPON, invalidates the LVGL screen, then turns the backlight ON
  after the redraw delay.

This wiring uses the module's own BLK input directly; no AO3400 is required.


### PC Monitor BLE + dynamic footer fix v1.10.1

- Protocol v3 fallback capabilities in the Windows app now include `PCMON`.
  This fixes Bluetooth sessions where the first GATT status read contains only
  `LUMIPAD|3|SAVER:...` or is temporarily unavailable.
- The app only reports PC Monitor as Live when both the configuration packet and
  telemetry packet were actually sent.
- The three large PC Monitor cards still use slots 1-3.
- The readable bottom row now uses slots 4-6 again instead of being hard-coded.
  Changing a footer slot in the app immediately changes the keyboard display.
- Footer text stays compact for the 101 px columns, for example:
  `CPU 52C`, `GPU 48C`, `RAM 43%`, `DOWN 12.4M`, `UP 850K`, `FPS 144`.


### ECO sleep and dynamic deep sleep v1.11.0

Power behavior is now split into user-configurable stages:

- **RGB idle timeout**: based only on physical key/encoder inactivity. The RGB
  LEDs can turn off before the display sleeps.
- **Screensaver timeout**: still does not replace the Now Playing screen while
  music is playing.
- **Soft sleep timeout**: still applies while music is playing. Example:
  screensaver 15 s + sleep 60 s keeps Now Playing visible until 60 s, then
  blanks the panel/backlight while BLE remains connected.
- **Deep sleep timeout**: configured in Settings. On battery power, firmware
  suspends ZMK devices and enters nRF52840 System OFF. Bluetooth disconnects;
  pressing a matrix key wakes/reboots the keyboard and the app reconnects.
- Background CFG/RGB/PC Monitor traffic does not count as wake activity.
  Soft sleep wakes only for physical key/encoder input, an Auto Profile switch,
  starting/opening music, or an explicit manual wake/show action.
- LVGL high-frequency timers are paused during soft sleep and the page polling
  loop drops from 500 ms to 2 s while asleep.


### Encoder rotation wake from deep sleep

Deep sleep now uses ZMK's soft-off wake-source flow instead of calling
`sys_poweroff()` directly.

Wake sources:
- Any matrix key.
- Encoder push (because it is part of the matrix).
- Encoder rotation in either direction.

Both EC11 phases P1.04 and P1.06 get dedicated wake-trigger devices that remain
suspended during normal operation and are armed only while entering deep sleep.


### One-click updater v1.12.0

Boot sequence:
- ST7789 BLK remains OFF for 0.5 s after the display UI is initialized.
- The splash is already rendered before BLK turns ON.
- The loading screen remains visibly on-screen for a full 2.5 s, then the
  normal DIAL DESK UI appears.

LumiPad Settings now includes:
- **Update firmware**: requires USB. LumiPad downloads the latest release
  `firmware.uf2`, commands the nice!nano into UF2 bootloader, detects the UF2
  drive, copies the firmware automatically, and reconnects after reboot.
- **Update app**: downloads the latest self-contained Windows ZIP, closes the
  running app, replaces its files, then launches LumiPad again automatically.

Release assets are produced by `.github/workflows/publish-latest.yml`.


### Automatic update notifications v1.13.0

- Firmware HELLO now reports `FW=<version>`.
- LumiPad checks the latest GitHub release at startup, after reconnect, after a
  firmware update, and every 30 minutes.
- Settings shows current and latest versions independently for the Windows app
  and keyboard firmware.
- Update buttons are enabled only when a newer version exists.
- Firmware update additionally requires USB; BLE users are prompted to plug in
  USB before the one-click UF2 flash begins.
- Older firmware without an FW version is treated as updateable so it can move
  onto the version-aware update system.


### Settings layout v1.13.1

- DEVICE is now the first card in the left Settings column.
- GENERAL is moved to the right column directly above SYSTEM.
- No Settings functionality changed.


### Unified app + firmware version v1.13.2

There is now one release-version source: the LumiPad app `<Version>` in
`pc-app/LumiPad.App/LumiPad.App.csproj`.

- CMake reads that value and injects it into firmware as
  `LUMI_FIRMWARE_VERSION`.
- Firmware HELLO therefore reports exactly the same version as the release.
- `release-manifest.json` uses the same version for app and firmware.
- The ZMK firmware workflow is triggered when the app project version changes.

Result: bumping the app from v1.13.2 to v1.13.3 automatically builds firmware
that reports `FW=1.13.3`, so LumiPad can immediately show and enable the
firmware Update button without manually editing a second version file.


### Unified RGB/display wake v1.13.3

RGB and the display now share the same meaningful wake sources while keeping
independent sleep timers.

Wake both display + RGB:
- Physical key press.
- Encoder push/rotation activity.
- Auto Profile actually changes profile.
- Media playback starts/opens.
- Explicit manual Wake / Show Screensaver action.

Do not wake either from background PC Monitor, telemetry or CFG/RGB sync traffic.

The RGB timeout remains independent from the display Sleep timeout. Every
meaningful wake resets the RGB idle timer, so RGB does not immediately turn
back off after Auto Profile or Media wakes the device.


### Fast Bluetooth Now Playing v1.14.0

Protocol v4 adds the `MEDIAFAST` capability.

- Now Playing metadata, transfer BEGIN and END commands remain acknowledged.
- Artwork payload chunks use paced BLE Write Without Response on protocol v4.
- Text bitmap chunks use Base64 (`TXTCHUNK64`) instead of HEX, reducing
  encoding overhead from 100% to roughly 33%.
- The fast writer uses short dynamic bursts (more conservative for a 20-byte
  BLE payload) so Windows does not flood the controller queue.
- Legacy protocol-v3 firmware automatically stays on the old reliable
  Write-With-Response path.
- Artwork remains 48x48 RGB332 over BLE; visual quality is unchanged.
- Diagnostics log the actual text/artwork transfer duration and negotiated BLE
  payload size for hardware verification.

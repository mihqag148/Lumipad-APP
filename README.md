# LumiPad Windows app

Nguồn chính của app desktop dùng chung cho **RYNOR ONE** và **PIXEL PRO**.
Toàn bộ 23 file của `pc-app/` được nhập nguyên vẹn từ RYNOR-ONE commit
`fd04bc68bd063b9c15b7db0b89902a97b0799e9d` trước khi cập nhật branding và release.

## Cấu trúc

- `pc-app/LumiPad.App/`: app WPF .NET 8, giao diện, services, USB/BLE/Raw HID drivers, updater, assets và VIA JSON đóng gói.
- `pc-app/README.md`: hướng dẫn sử dụng app.
- `docs/pre-split-history.md`: ghi chú app/protocol trước khi tách, giữ để tra cứu lịch sử.
- `.github/workflows/windows-app.yml`: build và phát hành app Windows độc lập.

Firmware/hardware nằm ở [RYNOR-ONE](https://github.com/mihqag148/RYNOR-ONE)
và [PIXEL PRO](https://github.com/mihqag148/PIXEL-PRO---Lumi-Macropad).
Không cần clone hai repo firmware để build app.

## Build trên Windows

Cài .NET 8 SDK rồi chạy từ root repo:

```powershell
dotnet restore pc-app/LumiPad.App/LumiPad.App.csproj
dotnet publish pc-app/LumiPad.App/LumiPad.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/LumiPad
```

File chạy vẫn là `Lumi Macropad.exe` để updater và cài đặt hiện có tương thích.
WebView2 Runtime cần có trên máy để dùng tab ZMK Studio/VIA.

## Actions và releases

Push/pull request build app và upload artifact `LumiPad-Windows-x64`.
Sau build thành công trên main, job release tạo `v<Version>` với
`LumiPad-Windows-x64.zip` và `release-manifest.json` (`appVersion`, không gộp firmware).
Version nằm trong `LumiPad.App.csproj`; tăng Version/AssemblyVersion/FileVersion
trước mỗi lần phát hành. Release đã tồn tại sẽ không bị ghi đè.

Updater đọc ba nguồn độc lập:

| Nội dung | Repo | Asset |
|---|---|---|
| App | `mihqag148/Lumipad-APP` | `LumiPad-Windows-x64.zip` |
| RYNOR ONE firmware | `mihqag148/RYNOR-ONE` | `firmware.uf2` |
| PIXEL PRO firmware | `mihqag148/PIXEL-PRO---Lumi-Macropad` | `PIXEL_PRO_OTA.bin`, `PIXEL_PRO_merged.bin` |

Bản app cũ trỏ vào repo firmware: tải bản app đầu tiên từ repo này và chạy một lần
để chuyển sang kênh cập nhật mới. Các release cũ được giữ nguyên.
Không thay đổi ID lưu cấu hình `dial-desk`, USB VID/PID, BLE UUID, protocol hay namespace.
Tên hiển thị DIAL DESK đã đổi thành RYNOR ONE.

`Definitions/PIXEL-PRO-VIA.json` được copy từ `via/pixel-pro-s2.json` của repo PIXEL PRO;
cần đồng bộ bản đóng gói khi thay đổi định nghĩa phần cứng. Firmware-side QMK adapter
nằm trong repo PIXEL PRO tại `qmk/lumi_raw_hid`.

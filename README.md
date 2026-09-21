# LumiPad App

Windows companion application for Lumi3D macro-control products.

## Products

- RYNOR ONE: keeps its existing serial/Bluetooth companion transport.
- PIXEL PRO: ESP32-S2 native USB device. Standard HID keyboard works independently; LumiPad communicates with the device through USB CDC serial.

## PIXEL PRO transport

LumiPad probes Windows COM ports, sends `HELLO`, and accepts only devices that reply with the PIXEL PRO protocol signature:

`PIXELPRO|1|...`

This keeps the application independent from the keyboard HID interface and makes USB diagnostics easy to inspect.

## Build

```powershell
dotnet restore pc-app/LumiPad.App/LumiPad.App.csproj
dotnet publish pc-app/LumiPad.App/LumiPad.App.csproj -c Release -r win-x64 --self-contained true
```

GitHub Actions publishes `LumiPad-Windows-x64.zip` from `main`.

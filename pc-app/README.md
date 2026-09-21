# LumiPad Windows app

.NET 8 WPF application for RYNOR ONE and PIXEL PRO.

PIXEL PRO uses two independent native USB functions:

1. HID keyboard for normal keys.
2. CDC serial for LumiPad commands, diagnostics and future display/profile transfers.

The app does not need to open or claim the keyboard interface. It finds PIXEL PRO by probing COM ports for the `PIXELPRO|1|` HELLO response.

Diagnostics show the selected COM port, protocol response, key events and serial errors.

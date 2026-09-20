# LumiPad Windows App

Windows companion app for RYNOR ONE and PIXEL PRO. See the [repository README](../README.md) for build and independent release instructions.

Current functions:

- Reads the Windows Now Playing / SMTC session from supported players.
- Sends title, artist, play/pause state, elapsed time and duration to the macropad.
- Shows an iPod-inspired Now Playing screen on the 320x172 ST7789.
- Controls the four WS2812B LEDs:
  - Auto by Layer
  - Rainbow
  - Warm Purple Ping-Pong
  - Orange Blink
  - Solid Color
  - LED On/Off
  - Brightness 5-50%
- Uses a second USB CDC serial interface, so ZMK Studio keeps its own USB serial connection.

## Use

1. Flash the firmware for your selected product from its own repository.
2. Connect the macropad to the Windows PC by USB.
3. Open `Lumi Macropad.exe` and select RYNOR ONE or PIXEL PRO.
4. Press Detect LumiPad if it is not detected automatically.
5. Start playing music.

Advanced Now Playing functions require the Windows app to stay running. Normal ZMK keyboard/BLE functionality does not.

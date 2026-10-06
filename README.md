# SyncBox

Windows desktop app that captures the screen (or a single window) and streams the
perimeter of that image to an addressable LED strip over USB serial or Wi-Fi UDP.

## Build and run

```
dotnet build SyncBox.slnx
dotnet test SyncBox.Tests/SyncBox.Tests.csproj
dotnet run --project SyncBox/SyncBox.csproj
```

Framework-dependent `net8.0-windows`, self-contained single-file publish profile in
`SyncBox/Properties/PublishProfiles/FolderProfile.pubxml` (output: `publish/win-x64`).

## LED layout

The strip is treated as a rectangular ring. `H` LEDs are sampled along each horizontal
edge and `V` along each vertical edge, so the ring holds `2 * (H + V)` LEDs. Pixels are
sent clockwise starting at the top-left corner:

| Order | Edge | Direction |
| --- | --- | --- |
| `0 .. H` | top | left to right |
| `H .. H+V` | right | top to bottom |
| `H+V .. 2H+V` | bottom | right to left |
| `2H+V .. 2H+2V` | left | bottom to top |

## Device discovery

The controller is identified by a handshake rather than by port name or USB VID/PID.
SyncBox writes the three-byte preamble **`AF 05 D1`** to a candidate port and waits for
the controller to answer with a line starting with **`Sync Box`**, for example
`Sync Box V1.0`. Anything else on the port (bootloader banners, other devices) is
ignored.

With *Pick the port automatically* enabled (the default), connecting over USB:

1. probes the port used last time first, then every other port Windows reports;
2. sends `AF 05 D1` at the selected baud rate and waits up to 600 ms per port;
3. if nothing answers at that rate, retries the remaining ports at 1000000, 115200 and
   500000 baud;
4. stops at the first baud rate that answers, switches the port selector to the winner,
   reports the firmware string in the interface card, and remembers the port.

The whole scan is capped at 10 seconds. If no controller answers, SyncBox falls back to
the manually selected port with a warning instead of refusing to connect, so firmware
that does not implement the handshake still works. *Detect* runs the same scan without
connecting.

## Wire protocol

Every frame is a fixed-overhead envelope followed by RGB triplets, in LED order.

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 1 | `0xAA` start of frame |
| 1 | 1 | `0x55` start of frame |
| 2 | 1 | opcode: `0x01` pixels, `0x02` ACK |
| 3 | 2 | LED count, little-endian `uint16` |
| 5 | `3 * count` | R, G, B bytes per LED |
| `5 + 3n` | 1 | CRC-8 (poly `0x07`, init `0x00`) over bytes `0 .. 5 + 3n - 1` |

Frame size is `5 + 3 * count + 1` bytes; the running total is shown in the LEDS tab.

An optional ACK mode is available. When enabled the app waits up to 12 ms for a
well-formed `0xAA 0x55 0x02` frame after each pixel frame and disables itself after
eight consecutive misses. Firmware only needs to answer with a valid envelope, for
example `AA 55 02 00 00 <crc>`. ACK mode costs frame rate, so it is off by default and
unavailable over UDP.

The frame rate limit (60 FPS by default, 0 for unlimited) is always applied, so the
capture loop never floods the link or pegs a core when no ACK is used.

## Colour handling

Captured pixels are sRGB. Each LED samples a small box average (1-5 px per side), then
the pipeline is: sRGB decode to linear light, saturation adjust, exponential smoothing,
brightness multiply, gamma exponent, sRGB re-encode. That keeps brightness and gamma
behaving like a display instead of stacking two gamma curves.

## Capture backends

* **GPU / DXGI** duplicates every display output and samples each one at its own
  virtual-desktop offset, so multi-monitor layouts (including monitors left of or above
  the primary) map correctly. Desktop duplication is re-created automatically when the
  driver reports `ACCESS_LOST`, `DEVICE_HUNG` or `DEVICE_REMOVED` (resolution changes,
  monitor swaps, fullscreen toggles).
* **CPU / GDI** blits the whole virtual desktop into a top-down 32-bit DIB section. It
  is the fallback when duplication is unavailable.

Both backends run the same sampler, which clamps every sample coordinate into the
captured surface, so an off-screen or destroyed target window degrades to full-desktop
capture instead of reading out of bounds.

## Settings

Stored as JSON at `%LOCALAPPDATA%\SyncBox\settings.json` and saved on connect,
disconnect and window close. Values are clamped on load, so a hand-edited or corrupt
file cannot produce an invalid frame size.
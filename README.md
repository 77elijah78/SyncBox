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

## Device discovery (USB serial)

The controller is identified by a handshake rather than by port name or USB VID/PID.
SyncBox writes the three-byte preamble **`AF 05 D1`** to a candidate port and waits for
the controller to answer with a line starting with **`Sync Box`**, for example
`Sync Box V1.0`. Anything else on the port (bootloader banners, other devices) is
ignored.

There is no manual port selection and no baud rate selector: detection always runs at
the controller's fixed **1,000,000 baud**, and connecting over USB runs the same scan
before it opens the link.

1. probes the port used last time first, then every other port Windows reports;
2. sends `AF 05 D1` at 1,000,000 baud and waits up to 600 ms per port;
3. switches the detected port to the winner, reports the firmware string in the
   interface card, and remembers the port.

The whole scan is capped at 10 seconds. If no controller answers, connecting fails with
a message instead of guessing a port, so a wrong port can never be opened silently.
*Detect* runs the same scan without connecting.

## Network discovery (devices on your router)

This is the default way to find the controller over Wi-Fi. It looks at the network the
PC is already connected to — the router's client list — instead of the Wi-Fi networks in
range.

With *Find the controller on my network automatically on connect* enabled (the default),
connecting over UDP:

1. collects every active IPv4 subnet of the PC (Ethernet and Wi-Fi adapters, skipping
   loopback, APIPA, tunnels and virtual adapters) and pings every address, capped at 1024
   hosts per subnet;
2. adds devices that never answer ICMP but still sit in the ARP cache;
3. asks every live address for its name (reverse DNS) and reads its MAC from the ARP
   table, so Espressif (ESP32/ESP8266) hardware can be recognised by its OUI;
4. sends the same `AF 05 D1` handshake used over serial to the configured UDP port and
   accepts any reply starting with `Sync Box`, which is the only definitive signal;
5. scores each device: handshake **100**, host name `SyncBox`/`Sync-Box-V1`-style
   **85-95**, Espressif MAC **55**, open LED port **60**, plain ping **10**.

Devices at or above **80** are picked automatically — so the same name-matching rules as
the serial path apply to the DHCP host name, and `Sync-Box-V1` matches because it starts
with `SyncBox` once separators and case are ignored. A device that only answers ping is
never assumed to be the controller; the *Devices on your network* list shows every
device found (IP, name, vendor) so it can be chosen manually, and choosing one fills the
*Controller IP* field. *Scan* runs the sweep without connecting.

If the sweep finds nothing, SyncBox falls back to the configured *Controller IP* with a
warning. Two things are worth checking when the controller is missing from the list:
the PC must be on the same router/subnet, and the Windows network profile should be
*Private* so inbound ICMP is not blocked by the firewall. Firmware that answers the
handshake on its UDP port is identified even when its name is unknown.

## Wi-Fi access point discovery (fallback)

Some controllers create their own access point instead of joining the router. The
*Wi-Fi networks in range* list shows **every** SSID Windows can see, with the names built
from `SyncBox` sorted to the top, so the names are never hidden. *Scan* refreshes the
list without connecting, and switching the interface to *WI-FI / UDP* scans at startup.

Joining is only a fallback: on connect SyncBox looks for the controller on the router
first, and only when that turns up nothing does it join the selected network (or the
best SyncBox match) and use its gateway as the controller IP. A network selected by hand
is joined even when its name has nothing to do with SyncBox.

1. scans every wireless interface (`WlanScan` + `WlanGetAvailableNetworkList`) and matches
   SSIDs after lower-casing and dropping separators, so `SyncBox`, `sync box`,
   `Sync-Box-V1` and `MySyncBox` all match while `BoxSync` does not;
2. orders the list: exact name first, then names starting with `SyncBox`, then names
   containing it; ties go to the network Windows is already on and then to signal strength;
3. joins the winner through `WlanConnect`. If Windows already has a stored profile it is
   reused (no elevation needed); otherwise a temporary profile is built in memory, so an
   unprivileged app can still join an open access point;
4. waits up to 12 s for the association, then reads the IPv4 gateway of the wireless
   adapter and uses it as the controller address. On an ESP-style access point that is
   `192.168.4.1`, so the *Controller IP* field follows the network automatically.

Secured access points need their password once. Type it in *Wi-Fi password* (8-63
characters, stored in `settings.json` next to the other settings) or join the network
once from the Windows Wi-Fi menu and let the stored profile be reused. WPA-PSK,
WPA2-PSK and WPA3-SAE are supported; WEP and 802.1X networks are reported instead of
guessed at.

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
file cannot produce an invalid frame size. The optional Wi-Fi password is part of that
file and is stored in plain text, so treat the file as sensitive. Network discovery is
controlled by `AutoFindOnNetwork`, and the access-point fallback by
`AutoDetectWifi`. `BaudRate` is always rewritten to 1,000,000 on load and save.
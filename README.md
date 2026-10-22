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

There is no port picker and no baud rate field in the interface: detection always runs
at the controller's fixed **1,000,000 baud**, and a single *Detect* line reports whether
a controller was found. Connecting runs the same scan before it opens the link.

1. probes the port used last time first, then every other port Windows reports;
2. sends `AF 05 D1` at 1,000,000 baud and waits up to 600 ms per port;
3. remembers the winning port and answers the highest-priority one found.

The whole scan is capped at 10 seconds. If no controller answers, connecting fails with
a message instead of guessing a port, so a wrong port can never be opened silently.

## Network discovery (devices on your router)

This is the default way to find the controller over Wi-Fi. Three independent sources are
merged so the list looks like the router's own client table:

1. **A ping of every address** on the local IPv4 subnets, 64 at a time with a 250 ms
   timeout. A router that answers ARP for the whole subnet (proxy ARP, common on guest and
   mobile networks) cannot fake this: only a powered device answers its own ping.
2. **The ARP table**, which supplies the MAC address of every device this PC has talked
   to. One `AF 05 D1` datagram is sent to every address plus a broadcast copy so the table
   fills in for hosts that were never contacted. A MAC used by many addresses belongs to
   the router, not to a device, and is dropped — that is what used to turn a /24 into 255
   fake rows.
3. **mDNS, SSDP and NetBIOS**, asked in parallel for 900 ms, which turn an address into a
   name (`Living Room TV`, `DESKTOP-ABC`, `SyncBox-D4F1`) and also reach devices a router
   hides behind proxy ARP. UPnP devices are asked for their `friendlyName`.

An address is listed when it is the gateway (*Router*), this PC, answered a ping or one of
the name questions, or has its own MAC address. A device without a name is still listed —
that is what a phone with a randomised MAC looks like — with its MAC in place of a name.
Addresses that exist only because the router answered ARP for them are dropped.

Scoring for the controller: handshake **100**, any answer on the LED port **92**, host name
`SyncBox`/`Sync-Box-V1` style **85-95** (same matching rules as the serial path), open LED
port **60**, Espressif MAC **55**, anything else **10**. Devices at or above **80** are
picked automatically. Choosing a row fills *Controller IP*; *Scan* runs the same lookup
without connecting. DNS lookups run in parallel with a 200 ms timeout and a 2.5 s budget,
so a scan finishes in about two seconds.

If the list is much shorter than the router's client list, the wireless network is
probably isolating clients (AP isolation or a guest network): those devices cannot reach
each other, so only the router is visible.

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

An optional ACK mode is available. When enabled the app waits up to 12 ms after each
pixel frame and disables itself after eight consecutive misses.

The controller can answer in either of two ways:

| Reply | Bytes | Notes |
| --- | --- | --- |
| Plain letter | `4F` (`O`) | One ASCII character, no header and no CRC. This is the simple option. |
| Binary frame | `AA 55 02 00 00 <crc>` | A normal envelope with opcode `0x02`, CRC included. |

Lowercase `o` (`6F`) is accepted as well. The letter is looked for directly in the bytes
received from the device, which is safe because the controller never sends pixels back;
binary frames are still parsed by the same reader as before. ACK mode costs frame rate,
so it is off by default and unavailable over UDP.

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

## Guide tab

The fourth tab is a short built-in guide: a five-step quick start (power, interface,
detect/scan, connect, start), a "good to know" list (fixed baud rate, discovery rules, LED
layout, ACK behaviour, settings persistence) and the three most common fixes (port busy,
controller missing from Wi-Fi, nothing lights up).

## Settings

Stored as JSON at `%LOCALAPPDATA%\SyncBox\settings.json` and saved on connect,
disconnect and window close. Values are clamped on load, so a hand-edited or corrupt
file cannot produce an invalid frame size. The optional Wi-Fi password is part of that
file and is stored in plain text, so treat the file as sensitive. Network discovery is
controlled by `AutoFindOnNetwork`, and the access-point fallback by
`AutoDetectWifi`. `BaudRate` is always rewritten to 1,000,000 on load and save.
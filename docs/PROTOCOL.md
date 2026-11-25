# USB-Display (VID 1908, PID 0102) protocol notes

Derived from a USBPcap capture of AIDA64 and confirmed with `src/UsbProbe`.

- Driver: libusb-win32 (`libusb0.sys`), configuration 1, interface 0.
- Endpoints: bulk OUT `0x01`, bulk IN `0x81`.
- Screen: 480x320, RGB565 **big-endian** on the wire (high byte first). Confirmed visually: little-endian made red/green/blue show as blue/pink/green.

## Transport (mass-storage style wrapper)

Every command is a 31-byte CBW on EP `0x01`:

| Bytes | Meaning |
|---|---|
| 0-3 | `USBC` |
| 4-7 | tag, always `DE AD BE EF` |
| 8-11 | data length (LE) |
| 12 | direction flag, always `00` (even for reads) |
| 13 | LUN `00` |
| 14 | CDB length `10` |
| 15-30 | 16-byte CDB, first byte `CD` |

Then data (OUT on `0x01` or IN on `0x81`), then a 13-byte CSW read from `0x81` (`USBS`, tag, residue, status byte 12 = 0 on success).

## Commands (CDB bytes after `CD 00 00 00 00`)

| CDB[5..] | Command | Data |
|---|---|---|
| `02 00 ...` | Get dimensions | 5 bytes IN: width LE16, height LE16, `FF` |
| `06 01 01 00 NN 00` | Set backlight, NN = 0..7 | none |
| `06 12 x0 y0 x1 y1 00` | Blit rectangle (x1/y1 inclusive, each LE16) | w*h*2 bytes OUT |

AIDA64 sends the full frame (`06 12 00 00 00 00 DF 01 3F 01 00`, 307200 bytes) in 64 KiB writes about once per second, and sets backlight 0 when it exits.

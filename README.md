# gterminal

A serial terminal emulator implemented as a single Gforth source file.
Connect to a serial device, type interactively, and upload local files by dragging and dropping them onto the terminal window, or source code by copy and paste.

## Requirements

- [Gforth](https://www.gnu.org/software/gforth/) 0.7.9 or later
- `unix/serial.fs` — included with Gforth
- `test/ttester.fs` — included with Gforth (used for the self-tests)

Tested with Gforth 0.7.9_20250912 on macOS (arm64) and 0.7.9_20240613 on Linux.

## Quick start

```bash
git clone https://github.com/uho/gterminal.git
cd gterminal
./connect                 # choose a serial device and start the terminal
```

Press **ESC twice** to leave the terminal.  To load gterminal without
connecting:

```bash
gforth ./gterminal.fs
```

The file loads, runs its self-tests, prints a banner, and leaves you at the Gforth prompt, where `connect` opens the connection.  For details, see [Connecting and device selection](#connecting-and-device-selection) below.

If Gforth is not on `PATH`, set `GFORTHPATH` to point at the Gforth source tree so that `unix/serial.fs` and `test/ttester.fs` can be found:

```bash
GFORTHPATH=/path/to/gforth gforth ./gterminal.fs -e connect
```

Write `./gterminal.fs`, not `gterminal.fs`: with `GFORTHPATH` set, Gforth searches only that path and does not find a file in the current directory.

### The `connect` script

`./connect` does all of this on Linux and macOS alike:

```sh
./connect                        # choose from the devices present
./connect /dev/cu.usbmodem101    # use this device
GFORTH=~/src/gforth/gforth ./connect
```

With no argument (or `select`) it calls `select-connect`: a lone serial device is taken without asking, several are offered as a menu.  `GFORTH` names the gforth executable (default `../gforth/gforth` next to the repository, else `gforth` from `PATH`).  If that executable lives in a gforth source tree, `GFORTHPATH` is set to that tree automatically.  Baud rate (460800) and upload settings are set once at the top of the script.

## Configuration

`serial-port-name` and `baud-rate` are selected automatically for the host operating system when the file loads, so the defaults usually work as-is:

| Platform | Default device      | Baud rate             |
|----------|---------------------|-----------------------|
| macOS    | `/dev/cu.usbserial` | 460800 (raw bits/s)   |
| Linux    | `/dev/ttyACM0`      | 460800 (`B460800`)    |

To use a different device, override the path after loading:

```forth
s" /dev/cu.usbmodem101" to serial-port-name
```

### A note on baud rates

Gforth's serial layer expects Linux *termios speed codes* (the `B*`constants), whereas macOS/BSD expect the raw bits-per-second.  gterminal hides this difference: the configured rate (default 460800) is translated for the running platform by `baud-for-os`.  To change the rate, edit the configured value near the top of `gterminal.fs` and, if that rate is not already known, add it to `linux-baud`:

```forth
: linux-baud ( bps -- code )
  case
    460800 of B460800 endof
    115200 of B115200 endof
    \ e.g. add:  921600 of B921600 endof
    -1 abort" linux-baud: unsupported baud rate"
  endcase ;
```

The available `B*` constants are `B50 … B57600 B115200 B230400 B460800
B500000 … B921600 … B4000000`.

## Connecting and device selection

```bash
gforth gterminal.fs -e connect
```

`connect` opens the configured port, forwards your keystrokes to the device, and writes incoming bytes to your terminal.  Press **ESC twice** to exit.

### The target is not reset when you leave

Exiting does **not** close the serial port.  The port stays open and the next `connect` goes on using it, so leaving and re-entering the terminal is invisible to the target.  A reused port is also not nudged with the CR that a freshly opened one gets to fetch a prompt.

Holding the port open is the only thing that helps.  Measured on macOS against a noForth tv# duo (RP2040, native USB CDC):

| what happens to the port | what the target does |
|---|---|
| a second `open()` while the port is open | nothing |
| closing one of two open descriptors | nothing |
| DTR **and** RTS dropped, then raised again | nothing |
| the **last** `close()` of the device | restarts: banner, empty stack |

So it is the last close that costs you the target's state, and no amount of care with the modem control lines avoids it.

gterminal also clears `HUPCL` in the port's `c_cflag` at open, as `stty -hupcl` does, which keeps the tty layer from hanging up the line on close.  That is what protects a board wired DTR→RESET behind an FTDI/CP210x adapter.  On the native-CDC board above it makes no difference — with `HUPCL` set or cleared, the last close restarts it just the same.

Release the device when you want it back — for another program, say:

```forth
close-port          \ close the serial port; targets like the above restart
```

Changing `serial-port-name` or `baud-rate` closes the old port by itself, so the next `connect` opens the device you just configured.  Leaving gforth with `bye` closes it too: the gforth session is what holds the target's state alive.

### Keeping the state across gforth runs, too

`bye` ends the process, and the kernel closes its descriptors — gterminal cannot prevent that from inside.  But the restart is bound to the *last* close of the device, so it is enough that **something else** holds the port.  Your shell will do, with one line and no background job:

```zsh
exec 9<>/dev/cu.usbmodem103   # once; the shell now holds the port
...                           # any number of gforth runs, ESC-ESC, bye, ...
exec 9>&-                     # done: release it (the target restarts)
```

Measured across two separate gforth runs with `bye` in between: the second `connect` printed no banner and the value pushed in the first run was still on the target's stack.  The shell never reads from that descriptor, so it takes nothing away from the terminal session.

Three things to keep in mind.  The `exec` line is itself an open: if nothing holds the port yet, it may restart the target, so run it before you build up state.  On macOS use the `/dev/cu.*` node; opening `/dev/tty.*` waits for carrier detect and the shell hangs.  And the hold lasts only as long as the shell: closing the terminal window is the last close, and after a reset or unplug the descriptor refers to a device that is gone — release it with `exec 9>&-` and open it again.

That close is also why opening is allowed to retry.  A target that restarts on the close re-enumerates, and for a moment its `/dev` node is present but refuses to open — so a port change would otherwise fail on a device that is merely coming back:

```forth
5   to open-retry-max       \ attempts before the error is reported
200 to open-retry-delay-ms  \ pause between them
```

The last attempt's error is still thrown, so a device that is really gone reaches `connect-failed` and the chooser exactly as before.

If the port cannot be opened, gterminal lists the serial devices currently present and lets you choose one:

```
1) /dev/cu.usbserial-1410
2) /dev/cu.usbmodem101
select device (1-2):
```

On Linux the devices are looked for under `/dev/serial/by-id` first, and only when there are none does the scan fall back to `/dev`:

```
1) /dev/serial/by-id/usb-1209_noForth_tv#_duo_251010-if00
2) /dev/serial/by-id/usb-1209_noForth_tv#_duo_251010-if02
```

Those names are made by udev from vendor, product, serial number and USB
interface, so they say *which* device — and which interface of it — rather than in what order things were plugged in.  A board that comes back under a different `ttyACM*`/`ttyUSB*` number keeps its by-id name, which is exactly the case where the reconnect below would otherwise give up and put the menu in front of you.  macOS has no `/dev/serial`, so the scan there finds nothing and the `/dev` fallback carries it, unchanged.

Type the number and press **Enter**; the chosen device becomes the active port and the connection is retried.  The list is sorted by path, so the same devices always get the same numbers — `read-dir` returns directory order, which varies from scan to scan, and a menu whose numbering moved would be a
trap.    If no matching device is present,
gterminal gives up.  The devices offered are matched per platform — Linux `ttyUSB*`/`ttyACM*`, macOS `cu.usbserial*`/`cu.usbmodem*` — under `/dev`.

When an established connection drops, gterminal first retries the same port once, quietly.  Only if that reconnect fails — for example a USB device that came back under a different `/dev/ttyUSB*` number on Linux — does the chooser appear again — and with the by-id names above, that case largely goes away on Linux too.  On macOS, where the device name is stable, the reconnect simply succeeds and no menu is shown.

Pressing the **reset button** on the target detaches its USB device: the `/dev` node disappears for a second or two and then the *same* node comes back. gterminal waits for exactly that port — by name — rescanning `/dev` and printing a dot per scan, then reopens it:

```
disconnected
waiting for /dev/cu.usbmodem14201 ....
```

So a reset is a short pause and an automatic reconnect, not `no serial devices found`, and no menu appears even when other serial devices are attached.  The chooser is still offered when the port really is gone — unplugged, or a Linux node that came back under a different `/dev/ttyUSB*` number — and when the port is there but keeps refusing to open, after `reconnect-max-tries` attempts:

```forth
5000 to device-wait-ms      \ how long to wait for the port to reappear (default)
500  to device-poll-ms      \ how often to rescan /dev while waiting
1000 to reconnect-delay-ms  \ pause between reconnect attempts
3    to reconnect-max-tries \ same-port retries before the chooser is offered
```

### Several gterminal instances

The chooser lists only devices that no other gterminal instance is using.  A noForth duo, for example, shows up as two serial devices.  Start `./connect` in two terminals: the first asks which one to use, and the second takes the remaining one without asking.  When every present device is in use, gterminal prints `all serial devices are in use` and gives up.  If you name a device another instance holds, the open is refused and the chooser appears.

Each instance holds an `flock` on `/tmp/gterminal-<device>.lock` (with `/` in the device path turned into `_`) for as long as it uses the device.  The device path is resolved first, so `/dev/serial/by-id/…` and the `/dev/ttyACM0` it points to share one lock.  The chooser checks these lock files and never opens a device to see whether it is busy, because on macOS closing a free interface of the duo would restart the whole chip, the other instance's session included.  The kernel drops an `flock` when its process ends, however that happens, so no stale lock is left behind.  The lock is released by `close-port`, when `connect` gives up, and by `bye`.  A drop keeps it, so no other instance takes over a device that is only resetting.  Programs other than gterminal (screen, picocom) do not take these locks and are not seen.

## File upload via drag and drop

Drag a file from your file manager onto the terminal window — the upload starts as soon as the path is recognised, no **Enter** needed.  gterminal detects the usual drag-and-drop path formats automatically:

| Format produced by terminal            | Example                    |
|----------------------------------------|----------------------------|
| bare path with trailing space          | `/path/to/file `           |
| double-quoted path with trailing space  | `"/path/to/file" `        |
| single-quoted path                     | `'/path/to/file'`          |
| path padded on both sides (Zed)        | ` /path/to/file `          |
| path with a trailing newline           | `/path/to/file` + LF       |

Surrounding spaces and matching quotes are stripped before the path is tested, so any combination of the paddings above works.  If the path resolves to a readable file, its contents are sent over the serial line.  Anything else is forwarded as ordinary keystrokes.

### How a drop is told apart from a paste

A drag-and-drop and a copy&paste of source code both arrive as a *burst*: a block of bytes already waiting on stdin, as opposed to the single byte a keypress delivers.  They cannot be distinguished character by character — Forth source is full of `/`, `"` and `'`, the very characters that start a path.  So gterminal collects the whole burst first and then asks the filesystem:

* one line that names a readable file → upload it
* anything else (several lines, no such file, a directory, longer than a path can be) → forward it to the receiver, paced byte by byte

A path *typed* by hand still works the old way: buffering starts at `/`, `"` or `'` and the decision is made when you press **Enter**.

File upload can be toggled at runtime:

```forth
disable-drop-upload   \ every burst is text; '/' and '"' forwarded like any key
enable-drop-upload    \ recognise dropped paths again (the default)
```

The two readings of the keyboard stream are separate hooks, so either can be replaced on its own:

```forth
' my-word is process-input   \ policy for one typed character ( char port -- )
' my-word is process-burst   \ policy for a whole burst      ( addr u -- )
' my-word is burst-byte      \ per-byte sink for burst text  ( char port -- )
```

### Upload pacing

A whole-file upload at a high baud rate can outrun a receiver that interprets the source line by line: bytes keep arriving while the receiver is busy with a line, its input buffer overruns, and the transfer is corrupted.  Three strategies are selectable (all used by drag-and-drop and `send-file`):

```forth
use-chunked-upload    \ default: send the file in 512-byte chunks, no handshake
use-eol-upload        \ same chunks, but line endings normalised to upload-eol
use-ack-upload        \ send one line, wait for the receiver to acknowledge it
```

`use-chunked-upload` is fast but unpaced; for a slow receiver, give it breathing room with `upload-chunk-delay` (milliseconds between chunks, default `0`):

```forth
20 upload-chunk-delay !
```

`use-eol-upload` fixes a different problem: line endings.  The chunked copy is byte-exact, so a source file saved with LF arrives as LF — and a receiver that only acts on `upload-eol` (CR by default) never executes a line.  Pasted text does not suffer from this, because the keyboard path already translates it (`lf>eol`); the upload path did not.  `use-eol-upload` closes that gap: it keeps the chunked pacing but turns every line ending the source may use — LF, CR or CRLF — into exactly one `upload-eol`.  All other bytes pass through unchanged, and a CRLF split across a chunk boundary is still collapsed to one terminator. Use the default `use-chunked-upload` when the bytes must arrive verbatim.

`use-ack-upload` removes the overrun entirely by letting the *receiver* set the pace.  It sends one line (terminated by `upload-eol`), then blocks until the receiver sends an **ACK** byte (line processed) before sending the next.  A **NACK** byte (the receiver hit an error) stops the upload immediately, so the failure surfaces instead of the rest of the file flooding past it.  The receiver must be programmed to emit these bytes.  The codes are configurable:

```forth
6  to upload-ack-char    \ byte the receiver sends after a line is processed OK
21 to upload-nack-char   \ byte the receiver sends when a line raised an error
13 to upload-eol         \ line terminator appended to each line sent (CR)
```

## Customisation hooks

Two deferred words let you intercept every byte in either direction without modifying the core loop:

```forth
' my-output-filter is process-output   \ ( char -- )        byte from serial
' my-input-filter  is process-input    \ ( char port -- )   keystroke from user
```

The connection lifecycle is pluggable too.  Both words below return a flag — `true` to give up, `false` to keep retrying — and default to the device chooser described above:

```forth
' my-handler is connect-failed   \ ( -- give-up? )  initial open failed
' my-handler is disconnected     \ ( -- give-up? )  established link lost
```

## Running the self-tests

The tests run automatically when the file is loaded.  To run them explicitly and exit:

```bash
GFORTHPATH=/path/to/gforth gforth ./gterminal.fs -e bye
```

A clean run prints only the startup banner.  Any failures are printed by the Hayes Tester (`ttester.fs`) before the banner.

## Example

`examples/hello.fs` is a small Forth file to try the upload with: connect to your target, then drag the file onto the terminal window.  The target prints `loaded`, and `test` counts from 0 to 9.

## Versions

gterminal follows [semantic versioning](https://semver.org).  Releases are tagged `vMAJOR.MINOR.PATCH`; the running version is shown in the boot banner and returned by `gterminal-version ( -- c-addr u )`.

## License

Copyright (C) 2026 Ulrich Hoffmann

gterminal is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.  See [LICENSE](LICENSE) for the full text.

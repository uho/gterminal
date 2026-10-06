\ gterminal.fs — Serial terminal emulator
\ Usage: gforth ./gterminal.fs
\
\ Copyright (C) 2026 Ulrich Hoffmann
\
\ This program is free software: you can redistribute it and/or modify
\ it under the terms of the GNU General Public License as published by
\ the Free Software Foundation, either version 3 of the License, or
\ (at your option) any later version.
\
\ This program is distributed in the hope that it will be useful,
\ but WITHOUT ANY WARRANTY; without even the implied warranty of
\ MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
\ GNU General Public License for more details.
\
\ You should have received a copy of the GNU General Public License
\ along with this program.  If not, see <https://www.gnu.org/licenses/>.

warnings off
require unix/serial.fs

\ Version of gterminal, semantic versioning (major.minor.patch).  A public
\ release is tagged v<version>; tools/sync-dist reads the version from here.
: gterminal-version ( -- c-addr u )  s" 1.0.0" ;

\ Print the boot banner: program name, version and how to use it.
: .banner ( -- )
  ." gterminal " gterminal-version type
  ."  - press ESC twice to exit, drag&drop a file to upload it" cr ;

\ ============================================================
\ Configuration
\ ============================================================

\ Serial port device path — defaulted per OS, override after loading if needed:
\   Linux:  /dev/ttyACM0           (also /dev/ttyUSB0 for some adapters)
\   macOS:  /dev/cu.usbserial-XXXX (suffix varies by adapter)

\ Choose the default device path for a gforth os-type string (see os-type).
: port-for-os ( os-addr os-u -- path-addr path-u )
  s" linux" string-prefix? if
    s" /dev/ttyACM0"
  else
    s" /dev/cu.usbserial"
  then ;

\ os-type string for the running gforth; empty if the query is unavailable.
: this-os ( -- c-addr u )
  s" os-type" environment? 0= if s" " then ;

this-os port-for-os 2value serial-port-name

\ Baud rate. serial.fs's B* constants are Linux termios speed *codes*, but
\ macOS/BSD termios expect the raw bits-per-second. baud-for-os bridges the
\ two: it returns the value open-serial expects on the running platform.

\ Map bits-per-second to the gforth (Linux) symbolic speed code.
: linux-baud ( bps -- code )
  case
    460800 of B460800 endof
    115200 of B115200 endof
    -1 abort" linux-baud: unsupported baud rate"
  endcase ;

\ Translate a desired rate (bps) to the speed argument for open-serial:
\ Linux wants the symbolic code; macOS/BSD want the raw bits-per-second.
: baud-for-os ( bps os-addr os-u -- speed )
  s" linux" string-prefix? if linux-baud then ;

460800 this-os baud-for-os value baud-rate

\ ============================================================
\ Byte tracing and pacing — shared by the keyboard and upload paths
\ ============================================================

\ Both paths need the same two things: a way to make an invisible byte stream
\ visible when something goes wrong, and a way to slow that stream down for a
\ receiver that cannot keep up. They live here, ahead of both users, so the
\ keyboard path and the upload path trace and pace identically.

\ Print char as two hex digits, leaving the current base untouched.
: .hex2 ( c -- )   base @ >r  hex  0 <# # # #> type  r> base ! ;

\ When upload-trace is on, every byte sent to the receiver is logged as ">hh"
\ and every byte received as "<hh" (2-digit hex), with an "L>" marker at each
\ line. This turns a mysterious hang into a visible record: run uploads until
\ one wedges, then read the tail to see exactly which byte was last sent and
\ what (if anything) came back. Off by default and never alters the byte stream.
variable upload-trace   upload-trace off
: trace-tx ( char -- )   upload-trace @ if  ." >" dup .hex2 space  then  drop ;
: trace-rx ( char -- )   upload-trace @ if  ." <" dup .hex2 space  then  drop ;
: trace-line ( -- )      upload-trace @ if  cr ." L> "  then ;

\ The same instrument for the keyboard path: with key-trace on, every byte the
\ keyboard forwards is logged as "k>hh". A paste is otherwise invisible — the
\ bytes never appear on screen unless the receiver echoes them — so this is the
\ only way to see what a paste actually delivered. Off by default.
variable key-trace   key-trace off
: trace-key ( char -- )  key-trace @ if  ." k>" dup .hex2 space  then  drop ;

\ Pause us microseconds (0 = no pause). Uses ns (nanosleep) so the gap can be
\ well under a millisecond, which ms (1 ms granularity) cannot express.
: pace-us ( us -- )   ?dup if  #1000 um* ns  then ;

\ ============================================================
\ ESC-ESC exit detection
\ ============================================================

\ Counts consecutive ESC (27) characters received from keyboard.
variable esc-count

\ Reset the ESC sequence counter.
: reset-esc ( -- )   0 esc-count ! ;

\ Return true when char completes the ESC-ESC exit sequence.
\ Resets the counter on any non-ESC character.
: check-esc ( char -- exit? )
  27 = if
    esc-count @ 1+ dup esc-count !
    2 >=
  else
    reset-esc false
  then ;

\ ============================================================
\ I/O Processing Hooks
\ ============================================================

\ Write one char to serial port and flush it; propagate I/O errors.
\ The flush is what makes pacing mean anything: without it emit-file only fills
\ gforth's buffer, so a paced burst still reaches the device as one blast and
\ can overrun the receiver exactly as an unpaced one would. The upload path has
\ always flushed per byte (see default-upload-emit); this is the same rule for
\ the keyboard path.
: serial-emit ( char port -- )
  dup >r emit-file ?ior
  r> flush-file ?ior ;

\ process-output: called for each byte arriving from serial port.
defer process-output   \ ( char -- )
' emit is process-output   \ default: display on terminal

\ process-input: called for each keystroke from the user.
defer process-input    \ ( char port -- )
' serial-emit is process-input   \ default: forward to serial port

\ ============================================================
\ Connection Loop
\ ============================================================

\ Open serial port file id during a connect session.
variable serial-port

\ Serial input words — deferred so tests can substitute simulated input.
: default-read-serial? ( -- f )    serial-port @ key?-file ;
: default-read-serial  ( -- char ) serial-port @ key-file ;
defer read-serial?   \ ( -- f )
defer read-serial    \ ( -- char )
' default-read-serial?  is read-serial?
' default-read-serial   is read-serial

\ Keyboard input words — deferred so tests can substitute simulated input.
defer read-key?   \ ( -- f )
defer read-key    \ ( -- char )
' key?  is read-key?
' key   is read-key

\ ------------------------------------------------------------
\ Serial hangup detection
\ ------------------------------------------------------------
\ macOS deletes the device node when the device resets, so the next serial
\ read fails hard (ENXIO) and key-file throws — connect catches that and
\ reconnects. Linux keeps the /dev/ttyACM* tty node alive and signals the
\ reset as a poll() hangup (POLLHUP) instead; FIONREAD then reports 0 bytes,
\ so key?-file says "no data", key-file is never called, nothing throws and
\ the connect loop spins forever. We close that gap by polling the port for
\ hangup/error conditions and throwing ourselves when one is seen, routing
\ Linux through the same reconnect path macOS already uses.

\ poll(2) revents bits not provided by libc.fs (POLLHUP is). Same values on
\ Linux and macOS/BSD.
$008 constant POLLERR    \ error condition on the fd
$020 constant POLLNVAL   \ fd not open

\ revents bits that mean the port is gone (hung up, errored, or closed).
POLLHUP POLLERR or POLLNVAL or constant POLL-DEAD

\ Thrown when a hung-up port is detected; any nonzero code makes connect's
\ catch route into disconnected → reconnect. Value chosen clear of gforth's
\ own throw codes.
-2000 constant hangup-throw

\ True if a poll() revents word indicates the port is dead.
: hangup-revents? ( revents -- f )   POLL-DEAD and 0<> ;

\ Poll the serial port (non-blocking) and return its revents word.
\ Returns 0 (no events) on a null port or a poll() error, so a genuine
\ hangup is only ever reported via the POLLHUP/POLLERR/POLLNVAL bits.
: port-revents ( port -- revents )
  dup 0= if drop 0 exit then
  fileno POLLIN port-poll fds!+ drop
  port-poll 1 0 poll 0< if 0 exit then   \ poll error: report no events
  port-poll revents w@ ;

\ True when the OS has hung up the serial port (device reset/removed/closed).
\ Deferred so tests can force the hung-up case without real hardware.
: default-port-hung-up? ( port -- f )   port-revents hangup-revents? ;
defer port-hung-up?   \ ( port -- f )
' default-port-hung-up? is port-hung-up?

\ Bytes drained from the port per turn of the connect loop. Draining a whole
\ block in one turn keeps the display up with a talkative receiver, but the
\ loop must stay bounded: a port that reports "data ready" forever — a regular
\ file at EOF, a device that died without a hangup — would otherwise spin here
\ and wedge the session.
256 value serial-drain-max

\ Forward the bytes waiting on the serial port to the display, at most
\ serial-drain-max of them. Throw first if the port has been hung up, so the
\ connect loop reconnects instead of spinning (Linux).
: process-serial ( -- )
  serial-port @ port-hung-up? if hangup-throw throw then
  serial-drain-max 0 ?do
    read-serial? 0= if leave then
    read-serial process-output
  loop ;

\ ------------------------------------------------------------
\ Paste bursts on the keyboard path
\ ------------------------------------------------------------
\ Typing delivers one byte per turn of the connect loop; a paste — and a
\ terminal drag-and-drop, which pastes the path — delivers a whole block at
\ once. Forwarding a burst one byte per loop turn is what broke pasting: the
\ bytes go out back-to-back at line speed with no pause, which can overrun a
\ small receiver buffer badly enough to wedge the receiver, and an ESC anywhere
\ in the pasted text counts toward the ESC-ESC exit. So we drain whatever is
\ already waiting in a single turn. That makes a burst recognisable as a burst,
\ and both rules follow from it:
\   - bytes in a burst are paced (key-char-delay); typed bytes need no pacing
\   - ESC only counts toward the exit sequence when it was typed, not pasted

500 value key-char-delay   \ microseconds to pause after each byte of a burst, to
                           \ keep a slow / small receiver buffer from overflowing.
                           \ 0 = full speed — which is what wedged a noForth board
                           \ mid-paste, hence a nonzero default. The same value
                           \ works on Linux and macOS.

\ Map one keyboard byte on its way to the receiver. Deferred so the line-ending
\ policy stays configurable; the real default (lf>eol) is installed further down,
\ once upload-eol exists.
: key-raw-xlat ( char -- char ) ;
defer key-xlat   \ ( char -- char )
' key-raw-xlat is key-xlat

\ Forward one typed keyboard byte to the serial port: translate, trace, then
\ hand it to the keystroke policy (process-input). Tracing after translation
\ logs what the receiver actually gets.
: key-send ( char -- )
  key-xlat dup trace-key
  serial-port @ process-input ;

\ ------------------------------------------------------------
\ Collecting a burst
\ ------------------------------------------------------------
\ A burst is collected in full before any of it is forwarded, so that it can be
\ classified as a whole (see process-burst). Deciding character by character is
\ what made drag-and-drop and a source-code paste indistinguishable: both start
\ with the same bytes.
\
\ 512 is more than a path can be — path-buf-max is 256 — so a burst that fills
\ this buffer is text by definition and needs no classification.
512 constant burst-buf-max
create burst-buf burst-buf-max allot
variable burst-buf-len
variable burst-text?    \ true once the burst is too long to be a path

: burst-buf-reset ( -- )   burst-buf-len off ;
: burst-buf-full? ( -- f ) burst-buf-len @ burst-buf-max >= ;

: burst-buf-add ( char -- )
  burst-buf-len @ burst-buf-max < if
    burst-buf burst-buf-len @ + c!
    1 burst-buf-len +!
  else
    drop
  then ;

\ Per-byte sink for the text reading of a burst. Deferred so tests can capture
\ the stream, and so the receiver policy stays replaceable.
defer burst-byte   \ ( char port -- )
' serial-emit is burst-byte

\ Forward one collected byte: translate, trace, send, then let the receiver
\ drain and show whatever it sent back.
: burst-byte-out ( char -- )
  key-xlat dup trace-key
  serial-port @ burst-byte
  process-serial
  key-char-delay pace-us ;   \ keep a small receiver buffer from overflowing

\ Forward a burst as text, paced byte by byte.
: send-burst-text ( addr u -- )
  over + swap ?do  i c@ burst-byte-out  loop ;

\ Policy for a complete burst. The default reads every burst as text;
\ enable-drop-upload replaces it with the classifier that also recognises a
\ dropped file path.
defer process-burst   \ ( addr u -- )
' send-burst-text is process-burst

\ Collect the whole burst, then hand it to process-burst. Entered with the
\ burst's first byte on the stack; read-key? has already confirmed that more
\ are waiting behind it. A burst that outgrows burst-buf cannot be a path, so
\ it is flushed as text as it arrives.
: send-burst ( char -- )
  burst-buf-reset  burst-text? off
  begin
    burst-buf-add
    burst-buf-full? if
      burst-text? on
      burst-buf burst-buf-len @ send-burst-text
      burst-buf-reset
    then
    read-key?
  while
    read-key
  repeat
  reset-esc                  \ a pasted ESC must not arm the exit for a typed one
  burst-buf burst-buf-len @
  burst-text? @ if  send-burst-text  else  process-burst  then ;

\ Forward what the keyboard has to offer; true when the user typed the ESC-ESC
\ exit sequence. A burst is drained in one call, so only a byte arriving alone
\ counts as typing — and only typing can trigger the exit.
: process-keyboard ( -- exit? )
  read-key? if
    read-key                        ( char )
    read-key? if
      send-burst false              \ more waiting: a paste, not a keypress
    else
      dup check-esc if
        drop true
      else
        key-send false
      then
    then
  else
    false
  then ;

\ ------------------------------------------------------------
\ Hang-up suppression (HUPCL)
\ ------------------------------------------------------------
\ When the last descriptor of a port is closed and HUPCL is set in c_cflag,
\ the tty layer hangs up the line — it drops DTR/RTS, and most target boards
\ read that as a reset.  Keeping the port open across sessions covers
\ connect/exit/connect, but not `bye`: there gforth is gone and the operating
\ system closes the descriptor for us.  Clearing HUPCL takes the hang-up out
\ of every close, gforth's exit included — the same thing `stty -hupcl` does.

\ c_cflag bit that makes a close hang up the line. OS-specific:
\ Linux uses octal 2000, macOS/BSD uses 0x4000.
: hupcl-for-os ( os-a os-u -- mask )
  s" linux" string-prefix? if $400 else $4000 then ;
this-os hupcl-for-os constant HUPCL

\ Remove the HUPCL bit from a termios c_cflag value.
: -hupcl ( cflag -- cflag' )   HUPCL invert and ;

\ Scratch termios for the c_cflag edit — not serial.fs's t_old/t_buf, which
\ hold the port's settings as found and as set by set-baud.
Create hup-termios termios allot

\ Clear HUPCL on an open port, so no close of it drops DTR/RTS. A no-op when
\ the descriptor is not a tty (the regular files the tests use).
: no-hangup ( fid -- )
  fileno { fd }
  fd hup-termios tcgetattr if exit then      \ not a tty: nothing to do
  hup-termios c_cflag dup flag@ -hupcl swap flag!
  fd 0 hup-termios tcsetattr drop ;

\ Deferred port lifecycle — override in tests to avoid real hardware.
defer open-port    \ ( addr u baud -- fid )
defer cleanup-port \ ( fid -- )

\ Open the configured device and take the hang-up out of it right away, so
\ that neither close-port nor gforth's exit resets the target.
: open-serial-nohup ( addr u baud -- fid )
  open-serial dup no-hangup ;

\ Swallow reset-baud errors so close-file is always attempted. On error catch
\ restores reset-baud's argument as well as the throw code, so normalise the
\ stack back to the entry depth instead of dropping a fixed count — dropping
\ just one cell would leak the restored fid on the error path.
: default-cleanup-port ( fid -- )
  { fid }
  depth { d0 }
  fid ['] reset-baud catch            \ run reset-baud, ignoring result and errors
  begin depth d0 > while drop repeat  \ drop the 0, or the restored arg + throw code
  fid no-hangup                       \ reset-baud put HUPCL back: take it out again
  fid close-file drop ;
' open-serial-nohup      is open-port
' default-cleanup-port   is cleanup-port

: hello ( -- ) 13 serial-port @ process-input ;

\ ------------------------------------------------------------
\ Port locks — which devices other gterminal instances hold
\ ------------------------------------------------------------
\ A board like the noForth duo shows up as two serial devices. With one of
\ them open in a first gterminal, a second one should offer only the other —
\ and take it without asking when it is the only one left. The device itself
\ cannot be asked: on macOS opening and closing a free interface restarts the
\ whole chip, and with it the session on its sibling. So every instance holds
\ an flock on a lock file named after the device it uses, and the chooser only
\ probes those lock files. The kernel drops an flock when its holder exits,
\ however it exits, so a crash leaves nothing stale behind.

c-library gterminal-lock
    \c #include <sys/file.h>
    \c #include <stdlib.h>
    c-value LOCK_EX LOCK_EX -- n ( -- n )
    c-value LOCK_NB LOCK_NB -- n ( -- n )
    c-function flock flock n n -- n ( fd op -- r )
    c-function realpath realpath s a -- a ( path-a path-u buf -- c-addr|0 )
end-c-library

\ Lock files are named lock-prefix + device path with "/" turned into "_".
s" /tmp/gterminal-" 2constant default-lock-prefix
default-lock-prefix 2value lock-prefix

\ The real path of a device, so that /dev/serial/by-id/... and the
\ /dev/ttyACM0 it points to share one lock. Unchanged when it cannot be
\ resolved (the device is not there).
4096 constant path-max
create real-path-buf path-max allot
: canonical-device ( dev-a dev-u -- dev-a' dev-u' )
  2dup real-path-buf realpath ?dup if  nip nip cstring>sstring  then ;

\ Build the lock file name for a device.
create lock-path-buf path-max allot
variable lock-path-len
: lock-path-add ( char -- )   lock-path-buf lock-path-len @ + c!  1 lock-path-len +! ;
: lock-path-add$ ( addr u -- )   bounds ?do  i c@ lock-path-add  loop ;
: lock-path ( dev-a dev-u -- path-a path-u )
  canonical-device
  0 lock-path-len !
  lock-prefix lock-path-add$
  bounds ?do
    i c@  dup [char] / = if  drop [char] _  then  lock-path-add
  loop
  s" .lock" lock-path-add$
  lock-path-buf lock-path-len @ ;

\ Is the lock file at path held by anyone? Probes with a lock of our own on a
\ fresh descriptor, which closing drops again. A missing file is not held.
: lock-held? ( path-a path-u -- f )
  r/o open-file if  drop false exit  then  { fid }
  fid fileno LOCK_EX LOCK_NB or flock 0<>
  fid close-file drop ;

\ The lock this instance holds: its open lock file and that file's path.
variable lock-fid   0 lock-fid !
create held-lock-buf path-max allot
variable held-lock-len   0 held-lock-len !
: held-lock ( -- path-a path-u )   held-lock-buf held-lock-len @ ;

\ Is the device in use by another instance? The one we hold ourselves is not.
: device-busy? ( dev-a dev-u -- f )
  lock-path  2dup held-lock compare 0= if  2drop false exit  then
  lock-held? ;

\ Let go of the lock we hold, if any.
: release-lock ( -- )
  lock-fid @ ?dup if  close-file drop  0 lock-fid !  then
  0 held-lock-len ! ;

s" serial device is in use by another gterminal" exception constant port-in-use-error

\ Open the lock file for path, creating it when it is not there yet.
: open-lock-file ( path-a path-u -- fid )
  2dup r/w open-file if  drop r/w create-file throw  else  nip nip  then ;

\ Claim a device for this instance, letting go of the one held before. Throws
\ port-in-use-error when another instance holds it.
: take-lock ( dev-a dev-u -- )
  lock-path  2dup held-lock compare 0= if  2drop exit  then   \ already ours
  release-lock
  2dup open-lock-file { fid }
  fid fileno LOCK_EX LOCK_NB or flock if
    2drop  fid close-file drop  port-in-use-error throw
  then
  fid lock-fid !
  dup held-lock-len !  held-lock-buf swap move ;

\ ------------------------------------------------------------
\ Keeping the port open across sessions
\ ------------------------------------------------------------
\ Closing a serial port drops DTR/RTS, and most target boards read that as a
\ reset — so leaving connect with ESC-ESC used to reboot the device, and
\ everything the user had built up there was gone before the next connect.  A
\ clean exit therefore leaves the port open: the descriptor stays in
\ serial-port and the next connect keeps using it.  The port is really closed
\ only when the configuration changed under it, when the device errored (it is
\ gone anyway), or when the user asks for it with close-port.

256 constant port-name-max
create open-port-name  port-name-max allot  \ device the open fid belongs to
variable open-port-baud                     \ speed it was opened with

\ True while a port descriptor is held in serial-port.
: port-open? ( -- f )   serial-port @ 0<> ;

\ Record which configuration the descriptor in serial-port was opened for.
: remember-port ( -- )
  serial-port-name port-name-max min open-port-name place
  baud-rate open-port-baud ! ;

\ Forget it, so nothing is taken for reusable after a close.
: forget-port ( -- )   0 open-port-name c!  0 open-port-baud ! ;
forget-port

\ True when the open descriptor still belongs to the configured device and
\ speed — only then may the next session go on using it.
: port-reusable? ( -- f )
  port-open?
  open-port-name count serial-port-name compare 0= and
  open-port-baud @ baud-rate = and ;

\ Close the port for real.  This resets most targets, so it is only done on
\ purpose.  A no-op when no port is open.  The device is free for other
\ instances afterwards.
: close-port ( -- )
  port-open? if  serial-port @  0 serial-port !  cleanup-port  then
  forget-port  release-lock ;

\ Let go of a port that errored: the device is gone, so hand back the
\ descriptor without touching termios — reset-baud on a dead port would only
\ throw again.  The lock is kept: the device is usually just resetting, and
\ another instance must not take it over before we reconnect.
: drop-port ( -- )
  port-open? if  serial-port @ close-file drop  0 serial-port !  then
  forget-port ;

\ How often an open may be retried before the error is reported, and the pause
\ between attempts.  Closing the port restarts some targets, and a target with
\ a native USB interface then re-enumerates: its /dev node is there but refuses
\ to open for a moment (ENXIO or EBUSY, measured at ~0.3 s on macOS).  A port
\ change closes one port and opens another, so the open lands in exactly that
\ window and would fail for a device that is merely coming back.
5   value open-retry-max
200 value open-retry-delay-ms

\ One attempt at the deferred open-port, reporting instead of throwing.
\ On failure catch has restored the arguments, so drop them with the code.
: try-open-port ( addr u baud -- fid true | ior false )
  ['] open-port catch ?dup if
    >r drop 2drop r> false
  else
    true
  then ;

\ Open the configured device, giving a node that is still re-enumerating a few
\ attempts to come back.  The last attempt's error is rethrown, so a device
\ that is really gone still reaches connect-failed as before — just later.
: open-port-retrying ( addr u baud -- fid )
  { addr u baud }
  open-retry-max 1 max { tries }
  tries 0 ?do
    addr u baud try-open-port if  unloop exit  then   ( ior )
    i tries 1- = if  unloop throw  then               \ out of attempts: report it
    drop  open-retry-delay-ms ms
  loop ;

\ Give the session a port: keep the open one when it still matches the
\ configuration, otherwise lock and open the configured device.  True when the
\ port was freshly opened, false when the previous session's port is being
\ reused.  Throws port-in-use-error when another instance holds the device.
: ensure-port ( -- opened? )
  port-reusable? if  false exit  then
  close-port                        \ stale: the device or the speed changed
  serial-port-name take-lock
  serial-port-name baud-rate open-port-retrying serial-port !
  remember-port  true ;

\ True once open-port has succeeded during the current connect call;
\ distinguishes "never connected" from "connection lost".
variable connected?

\ True while a reconnect attempt is in progress (a drop happened and the
\ silent retry has not yet succeeded). Cleared on every successful open.
variable reconnecting?

\ How many times the port that was in use has been retried since the drop, and
\ how often it may be retried before the chooser is offered. Without a bound, a
\ node that is present but refuses to open would be retried forever.
variable reconnect-tries
3 value reconnect-max-tries

\ Forget that a reconnect was in progress: called when a session opens and when
\ connect starts, so every drop begins its retry count from zero.
: reset-reconnect ( -- )   reconnecting? off  0 reconnect-tries ! ;

\ Open serial port and loop; returns normally on ESC-ESC, throws on I/O error.
\ The CR that fetches a prompt is only sent to a freshly opened port: a reused
\ one already carries a live session, which a stray CR would disturb.  On a
\ clean exit the port stays open — see close-port for why.
: connect-session ( -- )
  ensure-port { fresh? }
  connected? on  reset-reconnect
  reset-esc
  fresh? if hello then
  begin
    process-serial
    process-keyboard
  until ;

\ Delay in ms between reconnect attempts.
1000 value reconnect-delay-ms

\ connect calls run-session so tests can stub connect-session.
defer run-session \ ( -- )
' connect-session is run-session

\ Handle a failed initial connect (the port never opened). Returns true to
\ give up, false to keep retrying. Deferred so the behavior can be customized
\ later (e.g. wait and retry, or prompt the user).
: default-connect-failed ( -- give-up? )
  cr ." Cannot open " serial-port-name type cr
  true ;
defer connect-failed \ ( -- give-up? )
' default-connect-failed is connect-failed

\ Handle a lost connection (a device error after the link was established).
\ Returns true to give up, false to keep retrying. Deferred so the reconnect
\ behavior can be customized later.
: default-disconnected ( -- give-up? )
  cr ." disconnected" cr
  reconnect-delay-ms ms
  false ;
defer disconnected \ ( -- give-up? )
' default-disconnected is disconnected

\ ============================================================
\ Keyboard raw mode — let control keys reach the serial line
\ ============================================================

\ Without this, the local terminal turns Ctrl-C into SIGINT, which gforth
\ reports as throw -28; connect's catch then mistakes it for a device error
\ and drops into the reconnect loop. Clearing the ISIG bit on the keyboard
\ tty makes Ctrl-C (and Ctrl-Z/Ctrl-\) arrive as ordinary bytes that
\ process-input forwards to the serial line — the expected behaviour for a
\ terminal emulator (e.g. interrupting a program on the remote device).

\ c_lflag bit enabling signal-generating keys (Ctrl-C/Z/\). OS-specific:
\ Linux uses octal 1, macOS/BSD uses 0x80.
: isig-for-os ( os-a os-u -- mask )
  s" linux" string-prefix? if 1 else $80 then ;
this-os isig-for-os constant ISIG

\ Remove the ISIG bit from a termios c_lflag value.
: -isig ( lflag -- lflag' )   ISIG invert and ;

\ Keyboard (stdin) termios snapshots — separate from serial.fs's t_old/t_buf,
\ which hold the serial port's settings.
Create kbd-saved termios allot   \ keyboard settings as found
Create kbd-raw   termios allot   \ working copy with ISIG cleared
variable kbd-rawed               \ true while raw mode is in effect

\ Switch the keyboard to raw (ISIG off) so Ctrl-C reaches the serial line.
\ A no-op when stdin is not a terminal (e.g. during tests). key? primes
\ gforth's own terminal setup first, so our change is applied last and the
\ original (cooked) settings are restored cleanly on program exit.
: raw-keyboard ( -- )
  kbd-rawed off
  key? drop                                     \ let gforth prep the tty first
  stdin fileno kbd-saved tcgetattr if exit then \ not a tty: nothing to do
  kbd-saved kbd-raw termios move
  kbd-raw c_lflag dup flag@ -isig swap flag!
  stdin fileno 0 kbd-raw tcsetattr if exit then
  kbd-rawed on ;

\ Restore the keyboard to the settings raw-keyboard saved.
: restore-keyboard ( -- )
  kbd-rawed @ if
    stdin fileno 0 kbd-saved tcsetattr drop
    kbd-rawed off
  then ;

\ Reconnecting loop. Once a connection has been established, a device error
\ hands control to disconnected (retries by default). If the very first
\ connect never opened the port, hand control to connect-failed.
: connect ( -- )
  connected? off  reset-reconnect
  begin
    raw-keyboard
    ['] run-session catch
    restore-keyboard
    ?dup if
      drop
      drop-port
      connected? @ if
        disconnected       \ configurable: returns true to give up
      else
        connect-failed     \ configurable: returns true to give up
      then
    else
      true                 \ clean ESC-ESC exit
    then
  until
  port-open? 0= if  release-lock  then   \ gave up: the device is free again
  cr ;

\ ============================================================
\ Device selection (pluggable connect-failed / disconnected behavior)
\ ============================================================

\ Buffer read-dir reads each entry name into.
256 constant dir-name-max
create dir-name-buf  dir-name-max allot

\ Table of discovered device paths: device-max slots of device-slot bytes,
\ each holding a counted string (full path).
16  constant device-max
256 constant device-slot   \ a slot holds one path as a counted string. The
                           \ stable Linux names under /dev/serial/by-id carry
                           \ vendor, product, serial and interface, so they run
                           \ far past the length of a /dev/ttyACM0.
create device-table  device-max device-slot * allot
create scan-path-buf device-slot allot
variable device-count

\ Address of the i-th device slot.
: device[] ( i -- slot-a )   device-slot * device-table + ;

\ Does a /dev entry name look like a serial device for the given OS?
\ Linux: ttyUSB*/ttyACM*; macOS/BSD: cu.usbserial*/cu.usbmodem*.
: device-match-for-os ( name-a name-u os-a os-u -- f )
  s" linux" string-prefix? if
    2dup s" ttyUSB*" filename-match >r
         s" ttyACM*" filename-match r> or
  else
    2dup s" cu.usbserial*" filename-match >r
         s" cu.usbmodem*"  filename-match r> or
  then ;

\ ------------------------------------------------------------
\ Fixed device order
\ ------------------------------------------------------------
\ read-dir hands entries back in directory order, which is whatever the file
\ system finds convenient and which changes from scan to scan. The chooser
\ numbers the devices it prints, so an order that moves makes "2" a different
\ device from one menu to the next. Sorting the table by path makes both the
\ listing and the numbering reproducible.

create device-tmp device-slot allot   \ scratch slot for exchanging two entries

\ Exchange the contents of two device slots.
: device-swap ( i j -- )
  { i j }
  i device[]  device-tmp   device-slot move
  j device[]  i device[]   device-slot move
  device-tmp  j device[]   device-slot move ;

\ True when the path in slot j sorts before the one in slot j-1. Slot 0 has no
\ predecessor and is never out of order — checking that here keeps the sort
\ loop from reading the slot below the table.
: out-of-order? ( j -- f )
  dup 0= if  drop false exit  then
  { j }
  j 1- device[] count  j device[] count  compare 0> ;

\ Sort the device table by path. At most device-max (16) entries, so the
\ simplest sort that is plainly correct — insertion sort — is the right one.
: sort-devices ( -- )
  device-count @ 2 < if exit then     \ nothing to do, and ?do needs count > 1
  device-count @ 1 ?do
    i
    begin  dup out-of-order?  while   \ walk the entry down to where it belongs
      dup 1- over device-swap  1-
    repeat
    drop
  loop ;

\ True when dir/name fits a device slot. A slot holds a counted string, so the
\ path has to leave room for the length byte as well as the separating slash.
\ An entry that does not fit is left out rather than truncated: a cut-off path
\ names nothing that could be opened.
: path-fits? ( dir-u name-u -- f )   + 1+ device-slot 1- <= ;

\ Scan directory dir-a/dir-u, collecting full paths of entries for which
\ xt ( name-a name-u -- f ) is true into device-table/device-count, sorted.
: scan-dir ( dir-a dir-u xt -- )
  { dir-a dir-u xt }
  0 device-count !
  dir-a dir-u open-dir if drop exit then   ( wdirid )
  { dirid }
  begin
    dir-name-buf dir-name-max dirid read-dir  ( u2 flag wior )
    ?ior                                      ( u2 flag )
  while                                       ( u2 )
    dir-name-buf over xt execute if           ( u2 )
      dir-u over path-fits?                   \ ( u2 fits? )
      device-count @ device-max < and if      ( u2 )
        dir-a dir-u scan-path-buf place        \ path := dir
        s" /" scan-path-buf +place             \ path += "/"
        dir-name-buf over scan-path-buf +place \ path += name
        scan-path-buf count device-count @ device[] place
        1 device-count +!
      then
    then
    drop
  repeat                                      ( u2=0 )
  drop  dirid close-dir drop
  sort-devices ;

\ Predicate for the running OS.
: host-device? ( name-a name-u -- f )   this-os device-match-for-os ;

\ ------------------------------------------------------------
\ Where the devices are looked for
\ ------------------------------------------------------------
\ Linux keeps a stable symlink per device under /dev/serial/by-id, named after
\ vendor, product, serial number and USB interface:
\
\   usb-1209_noForth_tv#_duo_251010-if00 -> ../../ttyACM0
\   usb-1209_noForth_tv#_duo_251010-if02 -> ../../ttyACM1
\
\ That name is derived from the device, not from the order in which devices
\ were enumerated, so it survives a replug that renumbers ttyACM*/ttyUSB* —
\ exactly the case in which wait-for-port gives up and the chooser appears.
\ It also says which interface is which, which ttyACM0/ttyACM1 does not.
\
\ Both directories are 2values so a test can point a scan at its own files.
s" /dev/serial/by-id" 2value by-id-dir
s" /dev"              2value dev-dir

\ Everything under by-id is a serial device; usb-* keeps out . and .. and
\ whatever else may turn up there later.
: by-id-match? ( name-a name-u -- f )   s" usb-*" filename-match ;

\ Fill device-table from the by-id names. False when that directory is not
\ there at all (no udev, no device attached, and every non-Linux host) or
\ holds nothing we recognise.
: collect-by-id ( -- f )
  by-id-dir ['] by-id-match? scan-dir  device-count @ 0<> ;

\ Fill device-table from /dev, matching the platform's own device names.
: collect-dev ( -- )   dev-dir ['] host-device? scan-dir ;

\ Prefer the stable names, fall back to /dev when there are none. macOS needs
\ no special case: it has no /dev/serial, so the first scan finds nothing and
\ the fallback carries it. Deferred so tests can supply a fixed list.
: default-collect-devices ( -- )
  collect-by-id 0= if  collect-dev  then ;
defer collect-devices \ ( -- )
' default-collect-devices is collect-devices

\ Print the discovered devices as a numbered menu.
: show-devices ( -- )
  device-count @ 0 ?do
    cr i 1+ 0 .r ." ) " i device[] count type
  loop ;

\ Parse a menu selection (1-based) against count; index is 0-based.
: parse-choice ( input-a input-u count -- index ok? )
  { count }
  s>number? if                  ( n-low n-high )
    drop                        ( n )
    dup 1 count 1+ within if  1- true  else  drop 0 false  then
  else
    2drop 0 false
  then ;

\ Read the user's menu selection. Deferred so tests can script input.
: default-read-choice ( -- c-addr u )   pad 80 accept  pad swap ;
defer read-choice \ ( -- c-addr u )
' default-read-choice is read-choice

\ Make the chosen device the active port (copied to stable storage).
create chosen-buf device-slot allot
: apply-choice ( index -- )
  device[] count chosen-buf place
  chosen-buf count to serial-port-name ;

\ Remove the devices other instances hold from device-table, keeping the
\ order of the rest.
: drop-busy-devices ( -- )
  0 { kept }
  device-count @ 0 ?do
    i device[] count device-busy? 0= if
      i device[]  kept device[]  device-slot move
      kept 1+ to kept
    then
  loop
  kept device-count ! ;

\ List the present devices no other instance holds and let the user pick one.
\ Returns true to give up (no devices, all in use, or selection cancelled),
\ false to retry with the chosen port.
: choose-device ( -- give-up? )
  collect-devices
  device-count @ 0= if  cr ." no serial devices found" cr  true exit  then
  drop-busy-devices
  device-count @ 0= if  cr ." all serial devices are in use" cr  true exit  then
  device-count @ 1 = if
    0 apply-choice
    cr ." Now using " serial-port-name type cr
    false exit
  then
  show-devices
  cr ." select device (1-" device-count @ 0 .r ." ): "
  read-choice device-count @ parse-choice if
    apply-choice false
  else
    drop true
  then ;

\ Pick the port first, then connect: the chooser picks a lone device on its
\ own and shows the menu only when there are several. Used when no device was
\ named at startup (./connect select). Gives up quietly when nothing is chosen.
: select-connect ( -- )
  choose-device 0= if  connect  then ;

\ ------------------------------------------------------------
\ Waiting for the device in use to come back
\ ------------------------------------------------------------
\ Pressing the reset button on the target detaches its USB device: /dev loses
\ the node for a second or two, then the very same node reappears. A rescan
\ landing in that gap sees nothing, and the chooser would report "no serial
\ devices found" for a device that is merely rebooting — or, with more than one
\ device attached, put up a menu to ask for the port we were already using. So
\ we wait for that port by name and reopen it, and only fall back to the
\ chooser when it does not return.

500 value device-poll-ms   \ how often to rescan /dev while waiting
5000 value device-wait-ms  \ how long to keep waiting before giving up

\ Number of rescans that fit into the wait (1 max guards a zero poll interval,
\ which would otherwise never advance).
: device-poll-count ( -- n )   device-wait-ms device-poll-ms 1 max / ;

\ Is the port we were using among the devices the last scan found?
: port-present? ( -- f )
  device-count @ 0 ?do
    i device[] count serial-port-name compare 0= if  unloop true exit  then
  loop  false ;

\ Rescan /dev and report whether the port in use is back.
: port-back? ( -- f )   collect-devices  port-present? ;

\ Rescan until the port in use reappears or the wait is used up; true when it
\ is back. Leaves device-table holding the last scan, so a caller falling back
\ to the chooser sees whatever did show up. A dot per rescan, flushed so it
\ appears while waiting, makes the pause visibly a wait rather than a hang.
: wait-for-port ( -- f )
  port-back? if true exit then
  cr ." waiting for " serial-port-name type
  device-poll-count 0 ?do
    device-poll-ms ms
    ." ." stdout flush-file drop
    port-back? if  cr unloop true exit  then
  loop
  cr false ;

\ disconnected behavior: retry the current port silently once. If that reopen
\ fails, wait for the same device to come back and retry it — a reset brings
\ back the identical node, so the chooser would only be in the way. Only when
\ the port stays away (unplugged, or a Linux node renumbered to another
\ /dev/ttyUSB*) or keeps refusing to open do we offer the chooser.
: reconnect-or-choose ( -- give-up? )
  reconnecting? @ 0= if
    reset-reconnect  reconnecting? on
    cr ." disconnected" cr  reconnect-delay-ms ms  false exit
  then
  reconnect-delay-ms ms     \ the node can be back before it is openable again
  1 reconnect-tries +!
  reconnect-tries @ reconnect-max-tries <= if
    wait-for-port if  false exit  then     \ same device is back: reopen it
  then
  choose-device ;

\ ============================================================
\ File Upload
\ ============================================================

\ Buffer for reading source file in chunks.
512 constant upload-chunk-size
create upload-buf upload-chunk-size allot

\ Delay in milliseconds after each upload chunk (0 = no pacing).
\ Increase for slow receivers that need time to drain their input buffer.
variable upload-chunk-delay   0 upload-chunk-delay !

\ Copy all remaining bytes from src-fid to port in chunks, pacing each chunk
\ by upload-chunk-delay. Factored out of send-file-to so the latter can run it
\ under catch and still close the source file when a chunk write throws.
: copy-file-to ( src-fid port -- )
  { src-fid port }
  begin
    upload-buf upload-chunk-size src-fid read-file throw
    dup 0>
  while
    upload-buf swap port write-file throw
    port flush-file ?ior
    upload-chunk-delay @ ?dup if ms then    \ pace for slow receivers
  repeat
  drop
  port flush-file ?ior ;

\ ------------------------------------------------------------
\ ACK/NACK line-paced upload
\ ------------------------------------------------------------
\ A flow-controlled alternative to the chunk copy for a receiver that confirms
\ each line: it sends one ACK byte once a line has been fully processed, or one
\ NACK byte if the line raised an error. We send a line, wait for that ACK, and
\ only then send the next. A NACK stops the upload, so an error surfaces instead
\ of the rest of the file flooding past it. This removes the overrun entirely:
\ the receiver, not the baud rate, sets the pace.

6  value upload-ack-char    \ byte the receiver sends after a line is processed OK
21 value upload-nack-char   \ byte the receiver sends when a line raised an error
13 value upload-eol         \ line terminator appended to each line sent (CR)
10000 value upload-ack-timeout \ ms of total silence (no reply at all) before giving
                            \ up on an ACK; any received byte restarts it. 0 = wait forever
500 value upload-char-delay \ microseconds to pause after each body byte, to keep a
                            \ slow / small receiver buffer from overflowing. 0 = full
                            \ speed. Sub-millisecond on purpose: it pauses with ns
                            \ (nanosleep), which ms (1 ms granularity) cannot express.

\ Line-ending policy for the keyboard path. An editor paste carries LF (10), but
\ the receiver expects upload-eol — the very terminator the upload path appends
\ to every line — so a pasted line would otherwise never be executed. upload-eol
\ is read at run time, so retargeting it moves both paths together.
: lf>eol ( char -- char )   dup 10 = if  drop upload-eol  then ;

: use-key-eol ( -- )   ['] lf>eol       is key-xlat ;  \ default: pasted LF -> upload-eol
: use-key-raw ( -- )   ['] key-raw-xlat is key-xlat ;  \ send pasted bytes untouched

use-key-eol

\ Longest source line the ACK upload handles in one piece; longer lines are
\ split (an extra terminator is inserted), which is fine for Forth source.
256 constant ack-line-max
create ack-line-buf ack-line-max allot

\ Wall-clock time in milliseconds. Only differences are used, so the epoch and
\ any wrap are irrelevant. Deferred so tests can simulate the passage of time.
: default-upload-ticks ( -- ms )   utime 1000 ud/mod rot drop d>s ;
defer upload-ticks   \ ( -- ms )
' default-upload-ticks is upload-ticks

\ ------ upload byte trace (diagnostic) ------
\ upload-trace and its trace-tx / trace-rx / trace-line words are defined under
\ "Byte tracing and pacing" near the top of this file: the keyboard path needs
\ the same instrument and is defined long before this point.

\ Send one byte to the port and flush it. Deferred so tests can capture what is
\ sent and feed it back as the simulated echo.
: default-upload-emit ( char port -- )
  over trace-tx
  dup >r emit-file ?ior  r> flush-file ?ior ;
defer upload-emit   \ ( char port -- )
' default-upload-emit is upload-emit

\ Outcome of waiting for the receiver's per-line reply.
0 constant ack-ok         \ line acknowledged — send the next line
1 constant ack-nack       \ receiver reported an error — stop
2 constant ack-timed-out  \ no ACK/NACK arrived in time — stop

\ Block until the receiver acknowledges the line just sent, returning one of the
\ ack-* outcomes. Bytes that are neither ACK nor NACK (the receiver echoing the
\ line, or printing its prompt / "ok" / word output) are forwarded to the
\ display so the session stays live. The wait can never wedge — it is
\ interruptible two ways:
\   - upload-ack-timeout ms of total silence elapse (if set) -> ack-timed-out
\   - the port hangs up                                      -> hangup-throw (reconnect)
\ It is an inactivity timeout: any received byte restarts the clock, so long
\ output from the receiver won't false-trigger it.
: wait-ack ( port -- status )
  { port }
  upload-ticks { start }                      \ mark the start for the timeout
  begin
    port port-hung-up? if hangup-throw throw then
    read-serial? if
      read-serial dup trace-rx
      dup upload-ack-char  = if  drop ack-ok   exit  then
      dup upload-nack-char = if  drop ack-nack exit  then
      process-output                          \ ordinary output: show it...
      upload-ticks to start                   \ ...and count it as activity
    else
      1 ms                                    \ idle: don't peg the CPU
    then
    upload-ack-timeout ?dup if                \ 0 = wait forever
      upload-ticks start - swap u< 0= if  ack-timed-out exit  then
    then
  again ;

\ Human-readable reason an upload stopped early (ack-ok prints nothing).
: report-upload-stop ( status -- )
  case
    ack-nack      of cr ." upload aborted: receiver reported an error (NACK)" cr endof
    ack-timed-out of cr ." upload aborted: timed out waiting for the receiver" cr endof
  endcase ;

\ Send u bytes at addr through port one at a time, pausing upload-char-delay ms
\ after each. The per-byte gap keeps a slow receiver with a small input buffer
\ (e.g. noForth over USB-CDC) from overflowing mid-line and dropping a byte —
\ the intermittent-hang failure the per-line ACK alone cannot prevent. Unlike an
\ echo handshake, it makes no assumption about what the receiver sends back
\ (echo, prompt, "ok", word output all vary), it just paces the send by time.
\ Pause upload-char-delay microseconds (0 = no pause); see pace-us.
: upload-pace ( -- )   upload-char-delay pace-us ;

: send-line-paced ( addr u port -- )
  { addr u port }
  u 0 ?do
    addr i + c@ port upload-emit                        \ send one body byte (flushes)
    upload-pace                                         \ let the receiver drain it
  loop ;

\ Copy src-fid to port one line at a time: send the body time-paced, append the
\ terminator, then wait for the line's ACK. Any non-ACK outcome (NACK, timeout)
\ stops the upload and reports why (read-line strips the source's terminator).
: ack-copy-file-to ( src-fid port -- )
  { src-fid port }
  begin
    ack-line-buf ack-line-max src-fid read-line throw   ( u flag )
  while                                                 ( u )
    trace-line
    ack-line-buf swap port send-line-paced              \ send the line body, paced
    upload-eol port upload-emit                         \ and its terminator (flushes)
    port wait-ack dup ack-ok <> if                      \ stop on anything but ACK
      report-upload-stop exit
    then
    drop                                                \ ack-ok: keep going
  repeat
  drop ;

\ ------------------------------------------------------------
\ EOL-translating chunked upload
\ ------------------------------------------------------------
\ The chunked copy is byte-exact, so a file with LF line endings arrives as LF
\ even when the receiver only acts on upload-eol — the very translation lf>eol
\ already performs for pasted text. This strategy closes that gap for uploads:
\ same chunked pacing, but every line terminator the source may use (LF, CR or
\ CRLF) becomes exactly one upload-eol. Other bytes pass through unchanged, so
\ it is a line-ending normaliser, not a text filter.

\ Translated output for one input chunk. Translation never grows the data (each
\ input byte yields at most one output byte), so one chunk in fits one chunk out.
create eol-buf upload-chunk-size allot
variable eol-len

\ True while the previous byte was a CR, so a following LF is recognised as the
\ second half of a CRLF pair and swallowed. A variable, not a stack item,
\ because a CRLF pair can straddle a chunk boundary.
variable eol-pending-cr

: eol-out ( char -- )   eol-buf eol-len @ + c!  1 eol-len +! ;

\ Append one source byte to eol-buf in translated form.
: eol-xlat-byte ( char -- )
  dup 13 = if  drop  upload-eol eol-out  eol-pending-cr on  exit  then
  dup 10 = eol-pending-cr @ and if       \ LF right after CR: the CR already
    drop  eol-pending-cr off  exit       \ terminated the line, drop the LF
  then
  lf>eol eol-out  eol-pending-cr off ;

\ Translate u bytes at addr into eol-buf and return the translated bytes. The
\ CR state carries over between calls, so consecutive chunks translate as one
\ stream.
: eol-translate ( addr u -- addr' u' )
  0 eol-len !
  over + swap ?do  i c@ eol-xlat-byte  loop
  eol-buf eol-len @ ;

\ Like copy-file-to, but sends the translated bytes. Pacing is identical, so a
\ slow receiver is served by upload-chunk-delay here too.
: eol-copy-file-to ( src-fid port -- )
  { src-fid port }
  eol-pending-cr off                        \ each upload starts a fresh stream
  begin
    upload-buf upload-chunk-size src-fid read-file throw
    dup 0>
  while
    upload-buf swap eol-translate           ( addr u )
    port write-file throw
    port flush-file ?ior
    upload-chunk-delay @ ?dup if ms then    \ pace for slow receivers
  repeat
  drop
  port flush-file ?ior ;

\ Selectable upload strategy. Drag-and-drop and send-file both run upload-copy,
\ so switching it switches how every upload is sent. Default: chunked (fast, no
\ flow control, byte-exact); use-eol-upload keeps that pacing but normalises the
\ line endings to upload-eol; use-ack-upload selects ACK/NACK line pacing.
defer upload-copy   \ ( src-fid port -- )
' copy-file-to is upload-copy

: use-chunked-upload ( -- )   ['] copy-file-to      is upload-copy ;
: use-eol-upload     ( -- )   ['] eol-copy-file-to  is upload-copy ;
: use-ack-upload     ( -- )   ['] ack-copy-file-to  is upload-copy ;

\ Open file at addr/u and send all its bytes to port using the selected upload
\ strategy. The source file is always closed, even if the copy throws (e.g. the
\ port hung up mid-upload), so a failed upload neither leaks the descriptor nor
\ is silently retried.
: send-file-to ( addr u port -- )
  { port }
  r/o open-file ?ior { src-fid }
  src-fid port ['] upload-copy catch    \ run the chosen copy, but always reach...
  src-fid close-file drop               \ ...the close, then
  throw ;                               \ rethrow any error the copy raised

\ Send file at addr/u to the serial port, opening it if it is not open yet.
\ The port is left open afterwards: closing it would reset the target that has
\ just received the file.  Use close-port to release the device.
: send-file ( addr u -- )
    ensure-port drop
    serial-port @ send-file-to ;

\ ============================================================
\ Drop-upload
\ ============================================================

\ Buffer accumulates characters that look like a local file path.
256 constant path-buf-max
create path-buf path-buf-max allot
variable path-buf-len

: path-buf-reset ( -- )   path-buf-len off ;
: buffering?     ( -- f ) path-buf-len @ 0> ;
: path-buf-full? ( -- f ) path-buf-len @ path-buf-max >= ;

: path-buf-add ( char -- )
  path-buf-len @ path-buf-max < if
    path-buf path-buf-len @ + c!
    1 path-buf-len +!
  then ;

\ Strip trailing spaces from the path buffer (terminal drag-and-drop appends one).
: path-buf-rtrim ( -- )
  begin
    path-buf-len @ 0>
    path-buf path-buf-len @ 1- + c@  bl =
    and
  while
    -1 path-buf-len +!
  repeat ;

\ Strip leading spaces from the path buffer (Zed pads a dropped filename with
\ one on each side). Only ever applied to the scratch copy a drop candidate is
\ tested on, never to bytes that are about to be forwarded to the receiver.
: path-buf-ltrim ( -- )
  begin
    path-buf-len @ 0>
    path-buf c@ bl =
    and
  while
    path-buf 1+ path-buf path-buf-len @ 1- move
    -1 path-buf-len +!
  repeat ;

\ Strip surrounding matching quotes ("..." or '...') from path buffer if present
\ (quoted drag-and-drop). Opening and closing quote must be the same character.
: path-buf-unquote ( -- )
  path-buf-len @ 2 < if exit then
  path-buf c@ { q }
  q [char] " <> q [char] ' <> and if exit then          \ first char not a quote
  path-buf path-buf-len @ 1- + c@ q <> if exit then      \ last char must match opener
  path-buf 1+ path-buf path-buf-len @ 1- move
  -2 path-buf-len +! ;

\ True when the buffered path names a file whose bytes can actually be read.
\ Opening is not enough: a directory opens fine but fails on the first read, and
\ since a drop uploads immediately, a partially delivered path like "/tmp" must
\ not be mistaken for a file. An empty regular file reads 0 bytes without error
\ and does count.
: path-readable? ( -- f )
  path-buf path-buf-len @ r/o open-file if
    drop false
  else
    >r
    pad 1 r@ read-file nip 0=    \ ior 0: a real file, not a directory
    r> close-file drop
  then ;

\ Write buffered path bytes to port and reset the buffer.
: flush-buf-to-serial ( port -- )
  { port }
  path-buf path-buf-len @ port write-file throw
  path-buf-reset ;

\ On newline: upload if buffer holds a readable path, else forward as keystrokes.
: handle-newline ( char port -- )
  { ch port }
  buffering? if
    path-buf-rtrim
    path-buf-unquote
    path-readable? if
      path-buf path-buf-len @ 2>r   \ remember path (its bytes survive the reset)
      path-buf-reset                \ clear FIRST: if the upload throws, the path
      2r> port send-file-to         \ must not stay buffered for an accidental retry
    else
      port flush-buf-to-serial
      ch port serial-emit
    then
  else
    ch port serial-emit
  then ;

: del>bs ( ch -- ch )   dup 127 = if drop 8 then ;

\ Drop-in replacement for process-input.
\ Forwards non-path chars immediately; buffers from first '/', '"' or '\'' and uploads on newline.
: smart-process-input ( char port -- )
  { ch port }
  ch del>bs to ch
  ch 10 = ch 13 = or if
    ch port handle-newline
  else
    ch [char] / = ch [char] " = or ch [char] ' = or buffering? or if
      ch path-buf-add
      ch emit
      path-buf-full? if port flush-buf-to-serial then
    else
      ch port serial-emit
    then
  then ;

\ ------------------------------------------------------------
\ Recognising a dropped file path in a burst
\ ------------------------------------------------------------
\ A drag-and-drop and a source-code paste both reach us as a burst, so the two
\ cannot be told apart character by character: Forth source is full of '/', '"'
\ and the tick — the very characters that start path buffering when typed. The
\ filesystem is what separates them. A drop is a single line naming a readable
\ file; a paste of code is not, whatever it is made of.

\ True when the burst holds a line terminator anywhere but in its last
\ position. A drop delivers one line; a code paste almost always more.
: embedded-eol? ( addr u -- f )
  { addr u }
  u 0= if false exit then
  u 1- 0 ?do
    addr i + c@ dup 10 = swap 13 = or if
      true unloop exit
    then
  loop
  false ;

\ Copy the burst into path-buf so the path words can work on it;
\ false when it is longer than a path can be.
: burst>path-buf ( addr u -- f )
  { addr u }
  u path-buf-max > if false exit then
  addr path-buf u move
  u path-buf-len !
  true ;

\ Strip one trailing line terminator (a path pasted from an editor carries one).
: path-buf-strip-eol ( -- )
  path-buf-len @ 0= if exit then
  path-buf path-buf-len @ 1- + c@
  dup 10 = swap 13 = or if  -1 path-buf-len +!  then ;

\ True when the burst is a dropped file path, and then leaves the cleaned path
\ in path-buf. On any other burst path-buf is left empty, so a rejected burst
\ cannot leak into the next typed line.
: burst-drop? ( addr u -- f )
  path-buf-reset
  2dup embedded-eol? if 2drop false exit then
  burst>path-buf 0= if false exit then
  path-buf-strip-eol            \ an editor paste appends a newline
  path-buf-ltrim                \ Zed pads with a leading space,
  path-buf-rtrim                \ terminal drag-and-drop with a trailing one
  path-buf-unquote              \ ...and quotes a path that contains spaces
  path-readable? dup 0= if path-buf-reset then ;

\ Announce a drop before it is uploaded. The user typed nothing, so without
\ this the upload starts with no sign of which file was recognised. Deferred so
\ tests stay quiet.
: default-announce-upload ( addr u -- )   cr ." <upload " type ." >" cr ;
defer announce-upload   \ ( addr u -- )
' default-announce-upload is announce-upload

\ Burst policy with drop-upload on: a burst naming a readable file is uploaded
\ at once, every other burst is forwarded as text.
: dispatch-burst ( addr u -- )
  2dup burst-drop? if
    2drop
    path-buf path-buf-len @ 2>r    \ remember the path (its bytes survive the reset)
    path-buf-reset                 \ clear FIRST: a failed upload must not leave
    2r@ announce-upload            \ the path buffered for an accidental retry
    2r> serial-port @ send-file-to
  else
    send-burst-text
  then ;

\ Both readings of the keyboard stream are switched together: process-input is
\ the policy for a typed character, process-burst the one for a whole burst.
: enable-drop-upload  ( -- )
  ['] smart-process-input is process-input
  ['] dispatch-burst      is process-burst ;

: disable-drop-upload ( -- )
  ['] serial-emit         is process-input
  ['] send-burst-text     is process-burst ;

enable-drop-upload

true constant WANT-TESTS

WANT-TESTS [if]
\ ============================================================
\ Tests — run with: gforth gterminal.fs
\ ============================================================

require test/ttester.fs

\ Tests take real locks; keep them apart from a running instance's.
s" /tmp/gterminal-test-" to lock-prefix

VERBOSE OFF

TESTING ------ port-for-os ------

TESTING linux variants map to /dev/ttyACM0
T{ s" linux-gnu"     port-for-os  s" /dev/ttyACM0" compare -> 0 }T
T{ s" linux-musl"    port-for-os  s" /dev/ttyACM0" compare -> 0 }T
T{ s" linux-android" port-for-os  s" /dev/ttyACM0" compare -> 0 }T

TESTING non-linux (macOS) falls back to the usbserial default
T{ s" darwin24.0"    port-for-os  s" /dev/cu.usbserial" compare -> 0 }T

TESTING serial-port-name is set to a non-empty default
T{ serial-port-name nip 0> -> true }T

TESTING ------ baud-for-os ------

TESTING linux-baud maps bits-per-second to the symbolic speed code
T{ 460800 linux-baud -> B460800 }T
T{ 115200 linux-baud -> B115200 }T

TESTING linux uses the symbolic code, macOS/BSD uses the raw bps
T{ 460800 s" linux-gnu" baud-for-os -> B460800 }T
T{ 460800 s" darwin24.0" baud-for-os -> 460800 }T

TESTING baud-rate resolved to a valid value for this platform
T{ baud-rate 460800 = baud-rate B460800 = or -> true }T

TESTING ------ device selection ------

TESTING device-match-for-os: linux matches ttyUSB*/ttyACM*, rejects others
T{ s" ttyUSB0" s" linux-gnu" device-match-for-os -> true }T
T{ s" ttyACM0" s" linux-gnu" device-match-for-os -> true }T
T{ s" ttyS0"   s" linux-gnu" device-match-for-os -> false }T
T{ s" tty4"    s" linux-gnu" device-match-for-os -> false }T

TESTING device-match-for-os: macOS matches cu.usb*, rejects others
T{ s" cu.usbserial-1410" s" darwin24.0" device-match-for-os -> true }T
T{ s" cu.usbmodem1411"   s" darwin24.0" device-match-for-os -> true }T
T{ s" tty.usbserial"     s" darwin24.0" device-match-for-os -> false }T
T{ s" ttyUSB0"           s" darwin24.0" device-match-for-os -> false }T

TESTING parse-choice: valid 1-based selections map to 0-based index
T{ s" 1" 3 parse-choice -> 0 true }T
T{ s" 2" 3 parse-choice -> 1 true }T
T{ s" 3" 3 parse-choice -> 2 true }T

TESTING parse-choice: out-of-range, garbage and empty are rejected
T{ s" 0" 3 parse-choice -> 0 false }T
T{ s" 4" 3 parse-choice -> 0 false }T
T{ s" x" 3 parse-choice -> 0 false }T
T{ s" " 3 parse-choice -> 0 false }T

\ Create two matching and one non-matching file in /tmp for scan-dir.
s" /tmp/gtscanXA" w/o create-file throw close-file throw
s" /tmp/gtscanXB" w/o create-file throw close-file throw
s" /tmp/gtscanYC" w/o create-file throw close-file throw
: gtscanX? ( name-a name-u -- f )  s" gtscanX*" filename-match ;

TESTING scan-dir collects only matching entries
T{ s" /tmp" ' gtscanX? scan-dir  device-count @ -> 2 }T

TESTING scan-dir builds full directory-qualified paths
T{ 0 device[] count s" /tmp/gtscanX?" filename-match
   1 device[] count s" /tmp/gtscanX?" filename-match  and -> true }T

\ ------ fixed device order ------
\ Created out of alphabetical order on purpose; read-dir may hand them back in
\ any order at all, which is exactly what the sort has to absorb.
s" /tmp/gtsortD" w/o create-file throw close-file throw
s" /tmp/gtsortB" w/o create-file throw close-file throw
s" /tmp/gtsortC" w/o create-file throw close-file throw
s" /tmp/gtsortA" w/o create-file throw close-file throw
: gtsort? ( name-a name-u -- f )  s" gtsort*" filename-match ;

\ True when no entry sorts before its predecessor.
: devices-sorted? ( -- f )
  device-count @ 2 < if true exit then
  true
  device-count @ 1 ?do
    i out-of-order? if  drop false leave  then
  loop ;

: stub-collect-unsorted ( -- )
  3 device-count !
  s" /dev/ttyC" 0 device[] place
  s" /dev/ttyA" 1 device[] place
  s" /dev/ttyB" 2 device[] place ;

TESTING device-swap exchanges two slots
T{ stub-collect-unsorted  0 2 device-swap
   0 device[] count s" /dev/ttyB" compare
   2 device[] count s" /dev/ttyC" compare -> 0 0 }T

TESTING out-of-order? compares a slot with its predecessor, slot 0 never is
T{ stub-collect-unsorted  0 out-of-order? -> false }T
T{ stub-collect-unsorted  1 out-of-order? -> true }T    \ ttyA after ttyC
T{ stub-collect-unsorted  2 out-of-order? -> false }T   \ ttyB after ttyA

TESTING sort-devices puts the table in alphabetical order
T{ stub-collect-unsorted  sort-devices
   0 device[] count s" /dev/ttyA" compare
   1 device[] count s" /dev/ttyB" compare
   2 device[] count s" /dev/ttyC" compare -> 0 0 0 }T

TESTING sort-devices leaves a list of one or none alone
T{ 0 device-count !  sort-devices  device-count @ -> 0 }T
T{ 1 device-count !  s" /dev/ttyONLY" 0 device[] place  sort-devices
   0 device[] count s" /dev/ttyONLY" compare -> 0 }T

TESTING scan-dir hands the entries back in a fixed alphabetical order
T{ s" /tmp" ' gtsort? scan-dir  device-count @  devices-sorted? -> 4 true }T
T{ 0 device[] count s" /tmp/gtsortA" compare
   3 device[] count s" /tmp/gtsortD" compare -> 0 0 }T

\ ------ stable device names under /dev/serial/by-id ------
\ The real directory holds symlinks, but nothing ever resolves them — the scan
\ reads names only — so ordinary files in a directory of our own model it.

require mkdir.fs

s" /tmp/gtbyid"  $1FF mkdir-parents drop      \ already there: fine, ignore
s" /tmp/gtnodev" $1FF mkdir-parents drop      \ stays empty
s" /tmp/gtdev"   $1FF mkdir-parents drop
s" /tmp/gtbyid/usb-1209_noForth_tv#_duo_251010-if00" w/o create-file throw close-file throw
s" /tmp/gtbyid/usb-1209_noForth_tv#_duo_251010-if02" w/o create-file throw close-file throw
s" /tmp/gtbyid/pci-0000:00:1a.0-usb-0:1:1.0"         w/o create-file throw close-file throw
\ One device name per platform, so the fallback finds exactly one either way.
s" /tmp/gtdev/ttyACM0"        w/o create-file throw close-file throw
s" /tmp/gtdev/cu.usbmodemTST" w/o create-file throw close-file throw

TESTING by-id-match? takes the usb-* entries and leaves the rest
T{ s" usb-1209_noForth_tv#_duo_251010-if00" by-id-match? -> true }T
T{ s" pci-0000:00:1a.0-usb-0:1:1.0"         by-id-match? -> false }T
T{ s" ."  by-id-match? -> false }T
T{ s" .." by-id-match? -> false }T

TESTING collect-devices prefers the by-id names when they are there
T{ s" /tmp/gtbyid" to by-id-dir  s" /tmp/gtdev" to dev-dir
   default-collect-devices  device-count @ -> 2 }T
T{ 0 device[] count s" /tmp/gtbyid/usb-1209_noForth_tv#_duo_251010-if00" compare
   1 device[] count s" /tmp/gtbyid/usb-1209_noForth_tv#_duo_251010-if02" compare -> 0 0 }T

TESTING collect-devices falls back to /dev when the by-id directory is missing
T{ s" /tmp/gtbyid-does-not-exist" to by-id-dir
   default-collect-devices  device-count @ -> 1 }T
T{ 0 device[] count s" /tmp/gtdev/*" filename-match -> true }T

TESTING collect-devices falls back when the by-id directory holds nothing
T{ s" /tmp/gtnodev" to by-id-dir
   default-collect-devices  device-count @ -> 1 }T

T{ s" /dev/serial/by-id" to by-id-dir  s" /dev" to dev-dir -> }T

\ ------ a path too long for a slot is left out, not truncated ------
\ A name of 254 characters: with its directory and the separating slash the
\ path runs past the 255 a counted string can hold. Built as addr/u, not as a
\ counted string — place and +place clamp at 255 and would hide the very case
\ under test. Its own directory, so an entry that must not be collected cannot
\ disturb the fixtures above.

s" /tmp/gtlongdir" $1FF mkdir-parents drop
s" /tmp/gtlongdir/usb-ok" w/o create-file throw close-file throw

create gtlong-buf 320 allot
gtlong-buf 320 char a fill                        \ every byte an 'a' ...
s" /tmp/gtlongdir/usb-" gtlong-buf swap move      \ ... then stamp the head
: gtlong-path ( -- addr u )   gtlong-buf 269 ;    \ 19 + 250 a's; name is 254

TESTING path-fits? leaves room for the slash and the length byte
T{ 10 20 path-fits? -> true }T
T{ 11 243 path-fits? -> true }T                 \ 11 + 1 + 243 = 255, the most that fits
T{ 11 244 path-fits? -> false }T                \ one over
T{ device-slot device-slot path-fits? -> false }T

TESTING scan-dir leaves out an entry whose path would not fit a slot
T{ gtlong-path w/o create-file throw close-file throw
   s" /tmp/gtlongdir" ' by-id-match? scan-dir
   device-count @ -> 1 }T                       \ usb-ok kept, the long one left out
T{ 0 device[] count s" /tmp/gtlongdir/usb-ok" compare -> 0 }T

\ Stubs: fixed device lists and scripted selections.
: stub-collect-2 ( -- )
  2 device-count !
  s" /dev/ttyTEST0" 0 device[] place
  s" /dev/ttyTEST1" 1 device[] place ;
: stub-collect-1 ( -- )
  1 device-count !
  s" /dev/ttyONE" 0 device[] place ;
: stub-collect-0 ( -- )  0 device-count ! ;
: stub-pick-2 ( -- c-addr u )  s" 2" ;
\ Returns no selection; used to prove auto-select never consults read-choice.
: stub-pick-none ( -- c-addr u )  s" " ;

TESTING choose-device: a selection sets the port and asks to retry (false)
T{ ' stub-collect-2 is collect-devices
   ' stub-pick-2     is read-choice
   ' 2drop is type   ' noop is cr
   choose-device
   ' (type) is type  ' (cr) is cr
   -> false }T
T{ serial-port-name s" /dev/ttyTEST1" compare -> 0 }T

TESTING choose-device: a single device is selected automatically (false)
\ read-choice returns nothing, so if it were consulted choose-device would
\ give up (true) — expecting false proves the single device was auto-picked.
T{ ' stub-collect-1  is collect-devices
   ' stub-pick-none   is read-choice
   ' 2drop is type    ' noop is cr
   choose-device
   ' (type) is type   ' (cr) is cr
   -> false }T
T{ serial-port-name s" /dev/ttyONE" compare -> 0 }T

TESTING choose-device: no devices gives up (true)
T{ ' stub-collect-0 is collect-devices
   ' 2drop is type   ' noop is cr
   choose-device
   ' (type) is type  ' (cr) is cr
   -> true }T

\ Restore real device collection and the OS-default port.
T{ ' default-collect-devices is collect-devices
   ' default-read-choice     is read-choice
   this-os port-for-os to serial-port-name -> }T

TESTING reconnect-or-choose retries silently on the first drop (false)
T{ reconnecting? off
   ' 2drop is type  ' noop is cr  0 to reconnect-delay-ms
   reconnect-or-choose
   ' (type) is type  ' (cr) is cr
   -> false }T
T{ reconnecting? @ -> true }T

TESTING reconnect-or-choose offers the chooser once a reopen has failed
\ device-wait-ms 0: skip the reappear wait, this test is about the chooser.
T{ reconnecting? on  0 reconnect-tries !
   ' stub-collect-0 is collect-devices
   0 to device-wait-ms
   ' 2drop is type  ' noop is cr
   reconnect-or-choose
   ' (type) is type  ' (cr) is cr
   -> true }T
T{ ' default-collect-devices is collect-devices
   5000 to device-wait-ms
   1000 to reconnect-delay-ms -> }T

TESTING ------ waiting for the device in use to come back ------

variable collect-calls
\ Empty for the first two scans, the two test devices from the third on: a
\ target that vanishes on reset and reappears a moment later.
: stub-collect-late ( -- )
  1 collect-calls +!
  collect-calls @ 3 < if  0 device-count !  else  stub-collect-2  then ;
\ Selects the *first* device; used to prove the chooser was not consulted when
\ the port in use is expected to be reopened directly.
: stub-pick-1 ( -- c-addr u )  s" 1" ;

TESTING port-present? finds the port in use among the scanned devices
T{ stub-collect-2  s" /dev/ttyTEST1" to serial-port-name  port-present? -> true  }T
T{ stub-collect-2  s" /dev/ttyOTHER" to serial-port-name  port-present? -> false }T
T{ stub-collect-0  s" /dev/ttyTEST1" to serial-port-name  port-present? -> false }T

TESTING device-poll-count survives a zero poll interval (no endless wait)
T{ 1000 to device-wait-ms  0 to device-poll-ms  device-poll-count -> 1000 }T
T{ 1000 to device-wait-ms  500 to device-poll-ms  device-poll-count -> 2 }T

TESTING wait-for-port returns at once when the port is already there
T{ ' stub-collect-2 is collect-devices  0 collect-calls !
   s" /dev/ttyTEST1" to serial-port-name
   ' 2drop is type  ' noop is cr
   wait-for-port
   ' (type) is type  ' (cr) is cr
   -> true }T

TESTING wait-for-port keeps rescanning until the port reappears
T{ ' stub-collect-late is collect-devices  0 collect-calls !
   s" /dev/ttyTEST1" to serial-port-name
   1000 to device-wait-ms  0 to device-poll-ms
   ' 2drop is type  ' noop is cr
   wait-for-port
   ' (type) is type  ' (cr) is cr
   -> true }T
T{ collect-calls @ -> 3 }T   \ two empty scans, then the device is back

TESTING wait-for-port gives up after device-wait-ms if the port stays away
T{ ' stub-collect-2 is collect-devices
   s" /dev/ttyGONE" to serial-port-name        \ never in the scanned list
   100 to device-wait-ms  50 to device-poll-ms
   ' 2drop is type  ' noop is cr
   wait-for-port
   ' (type) is type  ' (cr) is cr
   -> false }T

TESTING a reset reopens the same device without going through the chooser
\ Two devices are present, so the chooser would put up a menu; read-choice is
\ scripted to pick the *other* one. Getting "retry" (false) with the port name
\ unchanged proves the port in use was reopened directly.
T{ reconnecting? on  0 reconnect-tries !
   ' stub-collect-late is collect-devices  0 collect-calls !
   ' stub-pick-1 is read-choice
   s" /dev/ttyTEST1" to serial-port-name
   1000 to device-wait-ms  0 to device-poll-ms  0 to reconnect-delay-ms
   ' 2drop is type  ' noop is cr
   reconnect-or-choose
   ' (type) is type  ' (cr) is cr
   -> false }T
T{ serial-port-name s" /dev/ttyTEST1" compare -> 0 }T   \ still the same device

TESTING a port that never comes back falls through to the chooser
T{ reconnecting? on  0 reconnect-tries !
   ' stub-collect-2 is collect-devices
   ' stub-pick-1 is read-choice
   s" /dev/ttyGONE" to serial-port-name
   100 to device-wait-ms  50 to device-poll-ms  0 to reconnect-delay-ms
   ' 2drop is type  ' noop is cr
   reconnect-or-choose
   ' (type) is type  ' (cr) is cr
   -> false }T                                   \ chooser picked a device
T{ serial-port-name s" /dev/ttyTEST0" compare -> 0 }T   \ the one read-choice named

TESTING retrying the same port is bounded: past the limit the chooser is offered
\ The port is present the whole time, so without the bound this would reopen it
\ forever. read-choice returns nothing, so reaching the chooser gives up (true).
T{ reconnecting? on  reconnect-max-tries reconnect-tries !
   ' stub-collect-2 is collect-devices
   ' stub-pick-none is read-choice
   s" /dev/ttyTEST1" to serial-port-name
   0 to reconnect-delay-ms
   ' 2drop is type  ' noop is cr
   reconnect-or-choose
   ' (type) is type  ' (cr) is cr
   -> true }T

TESTING reset-reconnect clears both the flag and the retry count
T{ reconnecting? on  5 reconnect-tries !  reset-reconnect
   reconnecting? @  reconnect-tries @ -> false 0 }T

\ Restore the defaults for the remaining tests.
T{ ' default-collect-devices is collect-devices
   ' default-read-choice     is read-choice
   this-os port-for-os to serial-port-name
   500 to device-poll-ms  5000 to device-wait-ms
   1000 to reconnect-delay-ms -> }T

TESTING non-ESC character: returns false, count stays 0
T{ reset-esc  65 check-esc -> false }T
T{ esc-count @ -> 0 }T

TESTING first ESC alone: returns false, count becomes 1
T{ reset-esc  27 check-esc -> false }T
T{ esc-count @ -> 1 }T

TESTING two consecutive ESCs: second returns true
T{ reset-esc  27 check-esc drop  27 check-esc -> true }T
T{ esc-count @ -> 2 }T

TESTING ESC followed by non-ESC: resets count to 0
T{ reset-esc  27 check-esc drop  65 check-esc -> false }T
T{ esc-count @ -> 0 }T

TESTING ESC, non-ESC, ESC: does not exit — ESCs must be consecutive
T{ reset-esc  27 check-esc drop  65 check-esc drop  27 check-esc -> false }T
T{ esc-count @ -> 1 }T

\ ------ process-output hook ------

variable test-char-out

: capture-output ( char -- )   test-char-out ! ;

TESTING override delivers char to the replacement word
T{ ' capture-output is process-output
   42 process-output  test-char-out @ -> 42 }T

TESTING restore default
T{ ' emit is process-output -> }T

\ ------ process-input hook ------

variable test-char-in
variable test-port-in

: capture-input ( char port -- )   test-port-in !  test-char-in ! ;

TESTING override receives both char and port
T{ ' capture-input is process-input
   65 7 process-input
   test-char-in @  test-port-in @ -> 65 7 }T

TESTING restore default
T{ ' serial-emit is process-input -> }T

TESTING ------ process-serial ------

variable sim-serial-value
variable sim-serial-ready

: sim-serial?  ( -- f )     sim-serial-ready @ ;
: sim-serial   ( -- char )  sim-serial-ready off  sim-serial-value @ ;

T{ ' sim-serial?  is read-serial?
   ' sim-serial   is read-serial
   ' capture-output is process-output -> }T

TESTING no serial data: process-output not called (sentinel -1 unchanged)
T{ sim-serial-ready off  -1 test-char-out !
   process-serial  test-char-out @ -> -1 }T

TESTING serial data available: process-output called with correct char
T{ 42 sim-serial-value !  sim-serial-ready on
   process-serial  test-char-out @ -> 42 }T

\ A port stuck on "data ready" — a regular file at EOF, or a device that died
\ without raising a hangup — must not spin here forever.
TESTING process-serial drains at most serial-drain-max bytes per call
variable drain-count
: always-serial? ( -- f )    true ;
: count-serial   ( -- char )  1 drain-count +!  65 ;
T{ ' always-serial? is read-serial?
   ' count-serial   is read-serial
   ' drop           is process-output
   drain-count off  process-serial  drain-count @ -> serial-drain-max }T

T{ ' default-read-serial?  is read-serial?
   ' default-read-serial   is read-serial
   ' emit is process-output -> }T

TESTING ------ serial hangup detection ------

TESTING hangup-revents?: hangup/error/closed bits mean dead, others do not
T{ 0       hangup-revents? -> false }T
T{ POLLIN  hangup-revents? -> false }T
T{ POLLHUP hangup-revents? -> true }T
T{ POLLERR hangup-revents? -> true }T
T{ POLLNVAL hangup-revents? -> true }T
T{ POLLIN POLLHUP or hangup-revents? -> true }T   \ data + hangup still counts as dead

TESTING port-hung-up?: a null port is not hung up (no fd to poll)
T{ 0 port-hung-up? -> false }T

TESTING port-hung-up?: a live, open file is not hung up (poll reports POLLIN)
s" /tmp/gterm-live.tmp" w/o create-file throw value gterm-live
T{ gterm-live port-hung-up? -> false }T
gterm-live close-file throw

TESTING process-serial throws hangup-throw when the port is hung up
\ Force port-hung-up? to report dead; the throw must fire before any read.
: stub-hung-up? ( port -- f )  drop true ;
T{ ' stub-hung-up? is port-hung-up?
   ' process-serial catch -> hangup-throw }T
\ Restore the real detector for any later tests / live use.
T{ ' default-port-hung-up? is port-hung-up? -> }T

TESTING ------ process-keyboard ------

variable sim-key-value
variable sim-key-ready

: sim-key?  ( -- f )     sim-key-ready @ ;
: sim-key   ( -- char )  sim-key-ready off  sim-key-value @ ;

T{ ' sim-key?  is read-key?
   ' sim-key   is read-key
   ' capture-input is process-input -> }T

TESTING no keyboard input: returns false
T{ sim-key-ready off  reset-esc  process-keyboard -> false }T

TESTING non-ESC key: returns false; process-input receives char and port
T{ 65 sim-key-value !  sim-key-ready on
   0 serial-port !
   process-keyboard -> false }T
T{ test-char-in @  test-port-in @ -> 65 0 }T

TESTING first ESC: returns false (not yet exit)
T{ reset-esc  27 sim-key-value !  sim-key-ready on
   process-keyboard -> false }T

TESTING second consecutive ESC: returns true (exit)
T{ 27 sim-key-value !  sim-key-ready on
   process-keyboard -> true }T

TESTING ------ paste burst on the keyboard path ------

\ A paste arrives as a burst: many bytes are already waiting on stdin, so
\ read-key? stays true after every byte but the last. That is exactly what
\ tells a burst apart from typing, and the simulated keyboard below reproduces
\ it by replaying a byte string.
256 constant sim-burst-max
create sim-burst-buf sim-burst-max allot
variable sim-burst-len
variable sim-burst-pos

\ Load addr/u as the bytes the simulated keyboard delivers next.
: sim-burst-load ( addr u -- )
  dup sim-burst-len !
  sim-burst-buf swap move
  sim-burst-pos off ;

: sim-burst-key? ( -- f )    sim-burst-pos @ sim-burst-len @ < ;
: sim-burst-key  ( -- char ) sim-burst-buf sim-burst-pos @ + c@  1 sim-burst-pos +! ;

\ Record every byte process-input forwards, in order, so a burst can be
\ checked for loss and reordering.
512 constant burst-cap-max
create burst-cap-buf burst-cap-max allot
variable burst-cap-len
: capture-burst ( char port -- )
  drop
  burst-cap-len @ burst-cap-max < if
    burst-cap-buf burst-cap-len @ + c!
    1 burst-cap-len +!
  else
    drop
  then ;

\ The captured bytes as a string.
: burst-cap ( -- addr u )   burst-cap-buf burst-cap-len @ ;

\ Run process-keyboard until the simulated keyboard runs dry or asks to exit.
: drain-keyboard ( -- exit? )
  begin
    sim-burst-key? while
    process-keyboard if true exit then
  repeat
  false ;

\ Feed addr/u as one burst; report whether the session asked to exit.
: feed-burst ( addr u -- exit? )
  sim-burst-load  burst-cap-len off  reset-esc  drain-keyboard ;

\ 200 printable bytes, free of LF and ESC so this case tests delivery only.
\ Filled by a colon definition: i is only meaningful in compiled code, so an
\ interpreted ?do loop would store through a garbage address.
create burst-200 200 allot
: build-burst-200 ( -- )
  200 0 ?do  33 i 90 mod +  burst-200 i + c!  loop ;
build-burst-200

\ "A" LF "B" LF — an editor paste of two lines.
create burst-lf 4 allot
char A burst-lf c!  10 burst-lf 1+ c!  char B burst-lf 2 + c!  10 burst-lf 3 + c!

\ The same two lines as the receiver should see them (LF -> upload-eol).
create want-eol 4 allot
: build-want-eol ( -- )
  [char] A want-eol c!       upload-eol want-eol 1+ c!
  [char] B want-eol 2 + c!   upload-eol want-eol 3 + c! ;

\ "A" ESC "B" — pasted text carrying a single escape byte.
create burst-esc 3 allot
char A burst-esc c!  27 burst-esc 1+ c!  char B burst-esc 2 + c!

\ "A" ESC ESC "B" — two adjacent escapes inside a paste (bracketed paste).
create burst-esc2 4 allot
char A burst-esc2 c!  27 burst-esc2 1+ c!  27 burst-esc2 2 + c!  char B burst-esc2 3 + c!

\ "A" ESC — a burst whose last byte is an escape.
create burst-tail-esc 2 allot
char A burst-tail-esc c!  27 burst-tail-esc 1+ c!

key-char-delay value saved-key-delay   \ restored after these tests

\ send-burst forwards each byte and then asks the serial port for a reply, so a
\ burst test needs a silent, live port: the real read-serial? would report
\ "data ready" forever on a regular file at EOF.
: burst-no-serial? ( -- f )      false ;
: burst-no-serial  ( -- char )   0 ;
: burst-port-live? ( port -- f ) drop false ;

T{ ' sim-burst-key?  is read-key?
   ' sim-burst-key   is read-key
   ' capture-burst   is burst-byte
   ' burst-no-serial? is read-serial?
   ' burst-no-serial  is read-serial
   ' burst-port-live? is port-hung-up?
   0 to key-char-delay -> }T

TESTING key-xlat maps LF to the receiver's terminator, other bytes unchanged
T{ 10 key-xlat -> upload-eol }T
T{ 65 key-xlat -> 65 }T
T{ 13 key-xlat -> 13 }T

TESTING a 200-byte burst is delivered complete and in order
T{ burst-200 200 feed-burst -> false }T
T{ burst-cap-len @ -> 200 }T
T{ burst-cap burst-200 200 compare -> 0 }T

TESTING a pasted LF is translated to upload-eol
T{ burst-lf 4 feed-burst -> false }T
T{ build-want-eol  burst-cap want-eol 4 compare -> 0 }T

TESTING use-key-raw leaves a pasted LF untouched
T{ use-key-raw  burst-lf 4 feed-burst -> false }T
T{ burst-cap-buf 1+ c@ -> 10 }T

TESTING use-key-eol restores the translation
T{ use-key-eol  burst-lf 4 feed-burst -> false }T
T{ burst-cap-buf 1+ c@ -> upload-eol }T

TESTING an ESC inside a paste burst does not end the session, and is forwarded
T{ burst-esc 3 feed-burst -> false }T
T{ burst-cap-len @ -> 3 }T
T{ burst-cap-buf 1+ c@ -> 27 }T

TESTING two adjacent ESCs inside a paste burst still do not end the session
T{ burst-esc2 4 feed-burst -> false }T
T{ burst-cap-len @ -> 4 }T

TESTING key-char-delay is settable and defaults to a nonzero pause
T{ saved-key-delay 0> -> true }T
T{ 250 to key-char-delay  key-char-delay -> 250 }T
T{ 0 to key-char-delay    key-char-delay -> 0 }T

TESTING the keyboard byte trace is off by default and never alters the stream
T{ key-trace @ -> false }T

TESTING a burst ending in ESC does not arm ESC-ESC for the next typed key
T{ burst-tail-esc 2 feed-burst -> false }T
T{ ' sim-key? is read-key?  ' sim-key is read-key
   27 sim-key-value !  sim-key-ready on
   process-keyboard -> false }T

TESTING two hand-typed ESCs still exit after a paste
T{ 27 sim-key-value !  sim-key-ready on
   process-keyboard -> true }T

T{ saved-key-delay to key-char-delay
   ' key?        is read-key?
   ' key         is read-key
   ' serial-emit is burst-byte
   ' serial-emit is process-input
   ' default-read-serial? is read-serial?
   ' default-read-serial  is read-serial
   ' default-port-hung-up? is port-hung-up? -> }T

TESTING ------ keyboard raw mode (ISIG / Ctrl-C) ------

TESTING isig-for-os: linux uses octal 1, macOS/BSD uses 0x80
T{ s" linux-gnu"  isig-for-os -> 1 }T
T{ s" linux-musl" isig-for-os -> 1 }T
T{ s" darwin24.0" isig-for-os -> $80 }T

TESTING ISIG resolved to this platform's value
T{ ISIG 1 = ISIG $80 = or -> true }T

TESTING -isig clears the ISIG bit and leaves the other bits untouched
T{ ISIG -isig -> 0 }T                 \ only ISIG set -> cleared to 0
T{ $1234 -isig -> $1234 }T            \ ISIG absent ($1234 has no ISIG bit) -> unchanged
T{ $1234 ISIG or -isig -> $1234 }T    \ ISIG removed, surrounding bits preserved

TESTING ------ hang-up suppression (HUPCL) ------

TESTING hupcl-for-os: linux uses octal 2000, macOS/BSD uses 0x4000
T{ s" linux-gnu"  hupcl-for-os -> $400 }T
T{ s" linux-musl" hupcl-for-os -> $400 }T
T{ s" darwin24.0" hupcl-for-os -> $4000 }T

TESTING HUPCL resolved to this platform's value
T{ HUPCL $400 = HUPCL $4000 = or -> true }T

TESTING -hupcl clears the HUPCL bit and leaves the other bits untouched
T{ HUPCL -hupcl -> 0 }T                 \ only HUPCL set -> cleared to 0
T{ $1234 -hupcl -> $1234 }T             \ HUPCL absent ($1234 has neither bit) -> unchanged
T{ $1234 HUPCL or -hupcl -> $1234 }T    \ HUPCL removed, surrounding bits preserved

\ The tests have no tty to work on, so no-hangup must simply do nothing on a
\ regular file — without throwing and without leaving the fid on the stack.
TESTING no-hangup is a silent no-op on a descriptor that is not a tty
T{ s" /tmp/gterm-hup.tmp" w/o create-file throw
   dup no-hangup  close-file drop -> }T

\ default-cleanup-port clears HUPCL once more after reset-baud, which would
\ otherwise put back the hang-up the open had taken out. The regular file here
\ makes both calls no-ops; what is tested is that neither throws nor unbalances.
TESTING default-cleanup-port still balances the stack with the no-hangup step
T{ s" /tmp/gterm-hup2.tmp" w/o create-file throw default-cleanup-port -> }T

TESTING open-port opens through the hang-up-suppressing opener by default
T{ ' open-port defer@ ' open-serial-nohup = -> true }T

TESTING ------ send-file-to ------

\ Read at most 256 bytes from a file path; used to verify test output.
256 constant read-back-size
create read-back-buf read-back-size allot

: ingest-file ( path-addr path-u -- addr u )
  r/o open-file throw { fid }
  read-back-buf read-back-size fid read-file throw { n }
  fid close-file drop
  read-back-buf n ;

variable upload-test-fid

\ write a source file
s" /tmp/gterminal-src.tmp" w/o create-file throw upload-test-fid !
s" Hello" upload-test-fid @ write-file throw
upload-test-fid @ close-file throw

\ send-file-to copies source to a destination port (regular file here)
s" /tmp/gterminal-dst.tmp" w/o create-file throw upload-test-fid !
T{ s" /tmp/gterminal-src.tmp" upload-test-fid @ send-file-to -> }T
upload-test-fid @ close-file throw
T{ s" /tmp/gterminal-dst.tmp" ingest-file  s" Hello" compare -> 0 }T

TESTING serial-emit flushes each byte, so pacing between bytes paces the device
\ Read the destination back through a second descriptor while the first is still
\ open: the byte is only visible there if serial-emit flushed it.
s" /tmp/gterm-flush.tmp" w/o create-file throw value gterm-flush
T{ char A gterm-flush serial-emit -> }T
T{ s" /tmp/gterm-flush.tmp" ingest-file  s" A" compare -> 0 }T
gterm-flush close-file throw

TESTING upload-chunk-delay defaults to 0 (no pacing)
T{ upload-chunk-delay @ -> 0 }T

TESTING send-file-to with a non-zero chunk delay still delivers identical bytes
20 upload-chunk-delay !
s" /tmp/gterminal-dlysrc.tmp" w/o create-file throw upload-test-fid !
s" delayed-payload" upload-test-fid @ write-file throw
upload-test-fid @ close-file throw
s" /tmp/gterminal-dlydst.tmp" w/o create-file throw upload-test-fid !
T{ s" /tmp/gterminal-dlysrc.tmp" upload-test-fid @ send-file-to -> }T
upload-test-fid @ close-file throw
T{ s" /tmp/gterminal-dlydst.tmp" ingest-file  s" delayed-payload" compare -> 0 }T
0 upload-chunk-delay !   \ restore default for remaining tests

TESTING send-file-to rethrows (does not swallow) a failed destination write
\ Readable source, but a read-only destination so the chunk write must fail.
s" /tmp/gterm-rosrc.tmp" w/o create-file throw upload-test-fid !
s" payload-data" upload-test-fid @ write-file throw
upload-test-fid @ close-file throw
s" /tmp/gterm-rodst.tmp" w/o create-file throw upload-test-fid !
upload-test-fid @ close-file throw
s" /tmp/gterm-rodst.tmp" r/o open-file throw value gterm-rodst
: send-to-ro ( -- )  s" /tmp/gterm-rosrc.tmp" gterm-rodst send-file-to ;
T{ ' send-to-ro catch 0<> -> true }T   \ error propagated, src closed en route
gterm-rodst close-file throw

TESTING ------ ACK/NACK upload ------

\ Scripted receiver: read-serial is always ready; sim-allack hands back an ACK
\ for every line, sim-nack2 ACKs the first line then NACKs the second.
variable ack-reads
: sim-ack?    ( -- f )   true ;
: sim-allack  ( -- ch )  1 ack-reads +!  upload-ack-char ;
: sim-nack2   ( -- ch )  1 ack-reads +!  ack-reads @ 2 < if upload-ack-char else upload-nack-char then ;

\ A three-line source file.
s" /tmp/gterm-ack-src.tmp" w/o create-file throw upload-test-fid !
s" L1" upload-test-fid @ write-line throw
s" L2" upload-test-fid @ write-line throw
s" L3" upload-test-fid @ write-line throw
upload-test-fid @ close-file throw

\ Pin the flow-control bytes so the expected output is explicit.
6 to upload-ack-char   21 to upload-nack-char   13 to upload-eol

TESTING use-ack-upload selects ACK pacing; every line is sent (each ACKed)
s" /tmp/gterm-ack-dst.tmp" w/o create-file throw value gterm-ack-dst
T{ ' sim-ack? is read-serial?   ' sim-allack is read-serial   ' drop is process-output
   0 ack-reads !  use-ack-upload
   s" /tmp/gterm-ack-src.tmp" gterm-ack-dst send-file-to -> }T
gterm-ack-dst close-file throw
T{ s" /tmp/gterm-ack-dst.tmp" ingest-file  s\" L1\rL2\rL3\r" compare -> 0 }T
T{ ack-reads @ -> 3 }T   \ one ACK consumed per line

TESTING NACK stops the upload before the offending line's successors
s" /tmp/gterm-nack-dst.tmp" w/o create-file throw value gterm-nack-dst
T{ ' sim-ack? is read-serial?   ' sim-nack2 is read-serial   ' drop is process-output
   0 ack-reads !  use-ack-upload
   ' 2drop is type  ' noop is cr          \ silence the abort message
   s" /tmp/gterm-ack-src.tmp" gterm-nack-dst send-file-to
   ' (type) is type  ' (cr) is cr  -> }T
gterm-nack-dst close-file throw
\ L1 ACKed (sent), L2 NACKed (sent, then stop) -> L3 never sent.
T{ s" /tmp/gterm-nack-dst.tmp" ingest-file  s\" L1\rL2\r" compare -> 0 }T

TESTING ACK upload with a non-zero per-character delay still delivers every byte
\ send-line-paced sends the body one byte at a time with upload-char-delay us
\ (sub-millisecond) between bytes; the file must be byte-identical to the burst.
s" /tmp/gterm-paced-dst.tmp" w/o create-file throw value gterm-paced-dst
T{ ' sim-ack? is read-serial?   ' sim-allack is read-serial   ' drop is process-output
   0 ack-reads !   250 to upload-char-delay   use-ack-upload
   s" /tmp/gterm-ack-src.tmp" gterm-paced-dst send-file-to -> }T
500 to upload-char-delay    \ restore default
gterm-paced-dst close-file throw
T{ s" /tmp/gterm-paced-dst.tmp" ingest-file  s\" L1\rL2\rL3\r" compare -> 0 }T
T{ ack-reads @ -> 3 }T      \ still one ACK per line — pacing is send-only

\ Deterministic clock and a silent receiver for the timeout test: every call to
\ the fake clock advances 50 ms, so a 100 ms timeout expires after two reads.
variable fake-ticks
: fake-ticks-now ( -- ms )  fake-ticks @  50 fake-ticks +! ;  \ advances 50 ms/read
: no-serial?     ( -- f )   false ;                          \ receiver never replies

TESTING wait-ack times out after upload-ack-timeout ms of total silence
s" /tmp/gterm-wa-live.tmp" w/o create-file throw value gterm-wa-live
T{ ' no-serial? is read-serial?   ' fake-ticks-now is upload-ticks
   0 fake-ticks !   100 to upload-ack-timeout
   gterm-wa-live wait-ack -> ack-timed-out }T
10000 to upload-ack-timeout   ' default-upload-ticks is upload-ticks   \ restore default
gterm-wa-live close-file throw

\ Restore default serial/output hooks and the chunked (default) upload strategy.
T{ ' default-read-serial?  is read-serial?
   ' default-read-serial   is read-serial
   ' emit is process-output
   use-chunked-upload -> }T

TESTING ------ EOL-translating upload ------

13 to upload-eol   \ pin the terminator so the expected bytes are explicit

\ eol-translate maps every line ending a source may use to one upload-eol.
T{ eol-pending-cr off  s\" a\nb" eol-translate    s\" a\rb"  compare -> 0 }T
T{ eol-pending-cr off  s\" a\r\nb" eol-translate  s\" a\rb"  compare -> 0 }T
T{ eol-pending-cr off  s\" a\rb" eol-translate    s\" a\rb"  compare -> 0 }T

TESTING eol-translate passes non-terminator bytes through unchanged
T{ eol-pending-cr off  s" : foo 1 2 + ;" eol-translate  s" : foo 1 2 + ;" compare -> 0 }T
T{ eol-pending-cr off  pad 0 eol-translate  nip -> 0 }T   \ empty chunk, empty result

TESTING a CRLF split across two chunks still yields a single upload-eol
\ No reset between the two calls: that is the point — the CR state carries over.
T{ eol-pending-cr off  s\" a\r" eol-translate  s\" a\r" compare -> 0 }T
T{ s\" \nb" eol-translate  s" b" compare -> 0 }T   \ the pending CR swallows the LF

TESTING consecutive LFs each become their own upload-eol (empty lines survive)
T{ eol-pending-cr off  s\" a\n\nb" eol-translate  s\" a\r\rb" compare -> 0 }T

\ An LF-terminated source file, as an editor on Linux/macOS writes it.
s" /tmp/gterm-eol-src.tmp" w/o create-file throw upload-test-fid !
s\" L1\nL2\nL3\n" upload-test-fid @ write-file throw
upload-test-fid @ close-file throw

TESTING the default (chunked) upload leaves LF line endings untouched
s" /tmp/gterm-eol-raw.tmp" w/o create-file throw value gterm-eol-raw
T{ use-chunked-upload
   s" /tmp/gterm-eol-src.tmp" gterm-eol-raw send-file-to -> }T
gterm-eol-raw close-file throw
T{ s" /tmp/gterm-eol-raw.tmp" ingest-file  s\" L1\nL2\nL3\n" compare -> 0 }T

TESTING use-eol-upload sends the same file with upload-eol line endings
s" /tmp/gterm-eol-dst.tmp" w/o create-file throw value gterm-eol-dst
T{ use-eol-upload
   s" /tmp/gterm-eol-src.tmp" gterm-eol-dst send-file-to -> }T
gterm-eol-dst close-file throw
T{ s" /tmp/gterm-eol-dst.tmp" ingest-file  s\" L1\rL2\rL3\r" compare -> 0 }T

TESTING use-eol-upload follows upload-eol rather than hard-coding CR
10 to upload-eol
s" /tmp/gterm-eol-lf.tmp" w/o create-file throw value gterm-eol-lf
T{ s" /tmp/gterm-eol-src.tmp" gterm-eol-lf send-file-to -> }T
gterm-eol-lf close-file throw
T{ s" /tmp/gterm-eol-lf.tmp" ingest-file  s\" L1\nL2\nL3\n" compare -> 0 }T
13 to upload-eol   \ restore

TESTING an EOL upload longer than one chunk translates across the boundary
\ 200 CRLF-terminated lines (~1400 bytes) span several 512-byte chunks, so a
\ pair is split by a boundary and must still collapse to one terminator.
s" /tmp/gterm-eol-big-src.tmp" w/o create-file throw upload-test-fid !
: write-crlf-lines ( n -- )   0 ?do  s\" line\r\n" upload-test-fid @ write-file throw  loop ;
200 write-crlf-lines
upload-test-fid @ close-file throw
s" /tmp/gterm-eol-big-dst.tmp" w/o create-file throw value gterm-eol-big
T{ s" /tmp/gterm-eol-big-src.tmp" gterm-eol-big send-file-to -> }T
gterm-eol-big close-file throw
\ Expect exactly 200 * len("line\r") bytes: no LF survived, no CR was doubled.
T{ s" /tmp/gterm-eol-big-dst.tmp" file-status nip -> 0 }T
T{ s" /tmp/gterm-eol-big-dst.tmp" r/o open-file throw dup file-size throw rot close-file throw
   -> 200 5 * 0 }T

use-chunked-upload   \ restore the default strategy for the remaining tests

\ send-file uses serial-port variable
\ s" /tmp/gterminal-dst2.tmp" w/o create-file throw serial-port !
\ T{ s" /tmp/gterminal-src.tmp" send-file -> }T
\ serial-port @ close-file drop
\ T{ s" /tmp/gterminal-dst2.tmp" ingest-file  s" Hello" compare -> 0 }T

TESTING ------ drop-upload ------

\ Helper: load a string directly into path-buf for test setup.
: >path-buf ( addr u -- )
  { addr u }  addr path-buf u move  u path-buf-len ! ;

\ del>bs converts DEL to BS, passes other chars unchanged
T{ 127 del>bs -> 8 }T
T{  65 del>bs -> 65 }T

TESTING DEL forwarded as BS via smart-process-input
s" /tmp/gterm-del.tmp" w/o create-file throw value gterm-del
T{ path-buf-reset  127 gterm-del smart-process-input  path-buf-len @ -> 0 }T
gterm-del close-file throw
T{ s" /tmp/gterm-del.tmp" ingest-file  drop c@ -> 8 }T

TESTING path-buf-reset clears the buffer
T{ path-buf-reset  path-buf-len @ -> 0 }T

TESTING buffering? tracks whether a path is being accumulated
T{ path-buf-reset  buffering? -> false }T
T{ char a path-buf-add  buffering? -> true }T

TESTING non-'/' char with empty buffer: forwarded immediately, buffer stays empty
s" /tmp/gterm-sp1.tmp" w/o create-file throw value gterm-sp1
T{ path-buf-reset  65 gterm-sp1 smart-process-input  path-buf-len @ -> 0 }T
gterm-sp1 close-file throw
T{ s" /tmp/gterm-sp1.tmp" ingest-file  s" A" compare -> 0 }T

TESTING '/' with empty buffer: buffering starts, nothing written to port yet
s" /tmp/gterm-sp2.tmp" w/o create-file throw value gterm-sp2
T{ ' drop is emit
   path-buf-reset  char / gterm-sp2 ' smart-process-input catch
   ' (emit) is emit
   throw
   path-buf-len @ -> 1 }T
gterm-sp2 close-file throw
T{ s" /tmp/gterm-sp2.tmp" ingest-file nip -> 0 }T

TESTING existing file + newline: upload triggered, buffer cleared
variable gterm-upsrc-fid
s" /tmp/gterm-upsrc.tmp" w/o create-file throw gterm-upsrc-fid !
s" upload-payload" gterm-upsrc-fid @ write-file throw
gterm-upsrc-fid @ close-file throw
s" /tmp/gterm-updst.tmp" w/o create-file throw value gterm-updst
T{ s" /tmp/gterm-upsrc.tmp" >path-buf -> }T
T{ 10 gterm-updst smart-process-input  path-buf-len @ -> 0 }T
gterm-updst close-file throw
T{ s" /tmp/gterm-updst.tmp" ingest-file  s" upload-payload" compare -> 0 }T

TESTING failed upload still clears path-buf (no re-transmission from the top on reconnect)
s" /tmp/gterm-htsrc.tmp" w/o create-file throw upload-test-fid !
s" retransmit-me" upload-test-fid @ write-file throw
upload-test-fid @ close-file throw
s" /tmp/gterm-htdst.tmp" w/o create-file throw upload-test-fid !
upload-test-fid @ close-file throw
s" /tmp/gterm-htdst.tmp" r/o open-file throw value gterm-htdst
\ A read-only destination makes the upload's write fail mid-stream, just as a
\ hung-up serial port would. handle-newline must have cleared the buffer anyway.
: trigger-failed-upload ( -- )
  s" /tmp/gterm-htsrc.tmp" >path-buf  10 gterm-htdst handle-newline ;
T{ ' trigger-failed-upload catch 0<> -> true }T   \ the upload did fail, and
T{ path-buf-len @ -> 0 }T                            \ ...the path was not left buffered
gterm-htdst close-file throw

TESTING non-existent path + newline: buffer flushed to port with newline, buffer cleared
s" /tmp/gterm-nofwd.tmp" w/o create-file throw value gterm-nofwd
T{ s" /no/such/xyz" >path-buf -> }T
T{ 10 gterm-nofwd smart-process-input  path-buf-len @ -> 0 }T
gterm-nofwd close-file throw
T{ s" /tmp/gterm-nofwd.tmp" ingest-file nip -> 13 }T   \ 12 path chars + newline

TESTING buffer overflow: all buffered chars flushed when full
path-buf-reset
path-buf path-buf-max 0 fill
path-buf-max 1- path-buf-len !
s" /tmp/gterm-sp4.tmp" w/o create-file throw value gterm-sp4
T{
   ' drop is emit
   char x gterm-sp4 ' smart-process-input catch
   ' (emit) is emit
   throw path-buf-len @ -> 0 }T
gterm-sp4 close-file throw
T{ s" /tmp/gterm-sp4.tmp" ingest-file nip -> path-buf-max }T

TESTING path-buf-rtrim strips trailing spaces
T{ s" /foo/bar " >path-buf  path-buf-rtrim  path-buf-len @ -> 8 }T
T{ s" /foo/bar"  >path-buf  path-buf-rtrim  path-buf-len @ -> 8 }T
T{ s" /foo   "  >path-buf  path-buf-rtrim  path-buf-len @ -> 4 }T

TESTING trailing space on dragged path: upload still triggered after trim
variable gterm-spaced-fid
s" /tmp/gterm-spaced.tmp" w/o create-file throw gterm-spaced-fid !
s" spaced-payload" gterm-spaced-fid @ write-file throw
gterm-spaced-fid @ close-file throw
s" /tmp/gterm-spaceddst.tmp" w/o create-file throw value gterm-spaceddst
T{ s" /tmp/gterm-spaced.tmp " >path-buf -> }T   \ trailing space
T{ 10 gterm-spaceddst smart-process-input  path-buf-len @ -> 0 }T
gterm-spaceddst close-file throw
T{ s" /tmp/gterm-spaceddst.tmp" ingest-file  s" spaced-payload" compare -> 0 }T

TESTING path-buf-unquote strips surrounding double-quotes
T{ s\" \"/foo/bar\"" >path-buf  path-buf-unquote  path-buf-len @ -> 8 }T
T{ path-buf 8 s" /foo/bar" compare -> 0 }T
T{ s" /foo/bar"  >path-buf  path-buf-unquote  path-buf-len @ -> 8 }T   \ no quotes: unchanged
T{ path-buf-reset  path-buf-unquote  path-buf-len @ -> 0 }T             \ empty: no crash

TESTING '"' with empty buffer: buffering starts, nothing written to port yet
s" /tmp/gterm-sq1.tmp" w/o create-file throw value gterm-sq1
T{ ' drop is emit
   path-buf-reset  char " gterm-sq1 ' smart-process-input catch
   ' (emit) is emit
   throw  path-buf-len @ -> 1 }T
gterm-sq1 close-file throw
T{ s" /tmp/gterm-sq1.tmp" ingest-file nip -> 0 }T

TESTING quoted existing file + newline: upload triggered, buffer cleared
variable gterm-qsrc-fid
s" /tmp/gterm-qsrc.tmp" w/o create-file throw gterm-qsrc-fid !
s" quoted-payload" gterm-qsrc-fid @ write-file throw
gterm-qsrc-fid @ close-file throw
s" /tmp/gterm-qdst.tmp" w/o create-file throw value gterm-qdst
T{ s\" \"/tmp/gterm-qsrc.tmp\"" >path-buf -> }T
T{ 10 gterm-qdst smart-process-input  path-buf-len @ -> 0 }T
gterm-qdst close-file throw
T{ s" /tmp/gterm-qdst.tmp" ingest-file  s" quoted-payload" compare -> 0 }T

TESTING quoted non-existent path + newline: buffer flushed to port with newline
s" /tmp/gterm-qnofwd.tmp" w/o create-file throw value gterm-qnofwd
T{ s\" \"/no/such/xyz\"" >path-buf -> }T
T{ 10 gterm-qnofwd smart-process-input  path-buf-len @ -> 0 }T
gterm-qnofwd close-file throw
T{ s" /tmp/gterm-qnofwd.tmp" ingest-file nip -> 13 }T   \ 12 unquoted path chars + newline

TESTING path-buf-unquote strips surrounding single-quotes
T{ s" '/foo/bar'" >path-buf  path-buf-unquote  path-buf-len @ -> 8 }T
T{ path-buf 8 s" /foo/bar" compare -> 0 }T

TESTING path-buf-unquote leaves mismatched quotes unchanged
T{ s\" '/foo/bar\"" >path-buf  path-buf-unquote  path-buf-len @ -> 10 }T  \ ' opener, " closer

TESTING ''' with empty buffer: buffering starts, nothing written to port yet
s" /tmp/gterm-sq2.tmp" w/o create-file throw value gterm-sq2
T{ ' drop is emit
   path-buf-reset  char ' gterm-sq2 ' smart-process-input catch
   ' (emit) is emit
   throw  path-buf-len @ -> 1 }T
gterm-sq2 close-file throw
T{ s" /tmp/gterm-sq2.tmp" ingest-file nip -> 0 }T

TESTING single-quoted existing file + newline: upload triggered, buffer cleared
variable gterm-sqsrc-fid
s" /tmp/gterm-sqsrc.tmp" w/o create-file throw gterm-sqsrc-fid !
s" squoted-payload" gterm-sqsrc-fid @ write-file throw
gterm-sqsrc-fid @ close-file throw
s" /tmp/gterm-sqdst.tmp" w/o create-file throw value gterm-sqdst
T{ s" '/tmp/gterm-sqsrc.tmp'" >path-buf -> }T
T{ 10 gterm-sqdst smart-process-input  path-buf-len @ -> 0 }T
gterm-sqdst close-file throw
T{ s" /tmp/gterm-sqdst.tmp" ingest-file  s" squoted-payload" compare -> 0 }T

TESTING single-quoted non-existent path + newline: buffer flushed to port with newline
s" /tmp/gterm-sqnofwd.tmp" w/o create-file throw value gterm-sqnofwd
T{ s" '/no/such/xyz'" >path-buf -> }T
T{ 10 gterm-sqnofwd smart-process-input  path-buf-len @ -> 0 }T
gterm-sqnofwd close-file throw
T{ s" /tmp/gterm-sqnofwd.tmp" ingest-file nip -> 13 }T   \ 12 unquoted path chars + newline

TESTING disable-drop-upload: '/' forwarded immediately via process-input, no buffering
s" /tmp/gterm-sp3.tmp" w/o create-file throw value gterm-sp3
T{ disable-drop-upload -> }T
T{ path-buf-reset  char / gterm-sp3 process-input  path-buf-len @ -> 0 }T
gterm-sp3 close-file throw
T{ s" /tmp/gterm-sp3.tmp" ingest-file  s" /" compare -> 0 }T
T{ enable-drop-upload -> }T

TESTING ------ burst: drop-upload vs. code paste ------

\ A drag-and-drop and a source-code paste arrive the same way: as a burst. The
\ classifier below is the only thing that tells them apart, so it gets tested
\ from both ends — the predicate itself, and a full trip through
\ process-keyboard with a simulated keyboard.

\ --- embedded-eol? : one line, or more than one? ---------------------------
T{ s" "                     embedded-eol? -> false }T
T{ s" /tmp/gterm-x"         embedded-eol? -> false }T
T{ s\" /tmp/gterm-x\n"      embedded-eol? -> false }T   \ trailing eol does not count
T{ s\" a\nb"                embedded-eol? -> true  }T
T{ s\" a\rb"                embedded-eol? -> true  }T
T{ s\" a\n\n"               embedded-eol? -> true  }T

\ --- burst-drop? : does the burst name a readable file? --------------------
variable bd-src-fid
s" /tmp/gterm-bsrc.tmp" w/o create-file throw bd-src-fid !
s" BURST-PAYLOAD" bd-src-fid @ write-file throw
bd-src-fid @ close-file throw

TESTING a bare path to an existing file is a drop, and is left in path-buf
T{ s" /tmp/gterm-bsrc.tmp" burst-drop? -> true }T
T{ path-buf path-buf-len @ s" /tmp/gterm-bsrc.tmp" compare -> 0 }T

TESTING path-buf-ltrim strips leading spaces, and only leading ones
T{ s"   /tmp/x " >path-buf  path-buf-ltrim
   path-buf path-buf-len @ s" /tmp/x " compare -> 0 }T
T{ s" /tmp/x" >path-buf  path-buf-ltrim
   path-buf path-buf-len @ s" /tmp/x" compare -> 0 }T
T{ s" " >path-buf  path-buf-ltrim  path-buf-len @ -> 0 }T

\ Zed pads a dropped filename with a leading *and* a trailing space.
TESTING a dropped path padded with spaces on both sides is a drop
T{ s"  /tmp/gterm-bsrc.tmp " burst-drop? -> true }T
T{ path-buf path-buf-len @ s" /tmp/gterm-bsrc.tmp" compare -> 0 }T
T{ s"  '/tmp/gterm-bsrc.tmp' " burst-drop? -> true }T
T{ path-buf path-buf-len @ s" /tmp/gterm-bsrc.tmp" compare -> 0 }T

TESTING a burst of nothing but spaces is not a drop
T{ s"    " burst-drop? -> false }T
T{ path-buf-len @ -> 0 }T

TESTING terminal drag-and-drop forms are recognised (trailing space, quotes, eol)
T{ s" /tmp/gterm-bsrc.tmp "     burst-drop? -> true }T
T{ s" '/tmp/gterm-bsrc.tmp'"    burst-drop? -> true }T
T{ s\" \"/tmp/gterm-bsrc.tmp\""  burst-drop? -> true }T
T{ s\" /tmp/gterm-bsrc.tmp\n"   burst-drop? -> true }T
T{ path-buf path-buf-len @ s" /tmp/gterm-bsrc.tmp" compare -> 0 }T

TESTING a non-existent path is not a drop, and leaves path-buf empty
T{ s" /no/such/gterm-file" burst-drop? -> false }T
T{ path-buf-len @ -> 0 }T

TESTING a directory is not a drop (it opens, but cannot be read)
T{ s" /tmp" burst-drop? -> false }T

\ This is the case the old per-character rule got wrong: '/' and the tick start
\ path buffering, so a pasted line of Forth was swallowed instead of forwarded.
TESTING a single line of Forth source is not a drop
T{ s" 1/2 ' foo bar"  burst-drop? -> false }T
T{ s\" : half 2/ ;\n' half\n" burst-drop? -> false }T

TESTING a burst longer than a path can be is not a drop
create bd-long 300 allot
: build-bd-long ( -- )  300 0 ?do  [char] a bd-long i + c!  loop ;
build-bd-long
T{ bd-long 300 burst-drop? -> false }T

\ --- dispatch-burst -------------------------------------------------------
: bd-quiet-announce ( addr u -- )  2drop ;
' bd-quiet-announce is announce-upload
variable bd-saved-port
serial-port @ bd-saved-port !

TESTING a dropped path burst uploads the file's contents, not the path
s" /tmp/gterm-bdst.tmp" w/o create-file throw serial-port !
T{ s" /tmp/gterm-bsrc.tmp" dispatch-burst -> }T
serial-port @ close-file throw
T{ s" /tmp/gterm-bdst.tmp" ingest-file  s" BURST-PAYLOAD" compare -> 0 }T
T{ path-buf-len @ -> 0 }T

TESTING a code-paste burst is forwarded verbatim, and uploads nothing
T{ ' capture-burst is burst-byte
   ' burst-no-serial? is read-serial?
   ' burst-no-serial  is read-serial
   ' burst-port-live? is port-hung-up?
   0 to key-char-delay
   burst-cap-len off
   s" 1/2 ' foo bar" dispatch-burst
   burst-cap s" 1/2 ' foo bar" compare -> 0 }T

TESTING with drop-upload off a path burst is forwarded as text
T{ disable-drop-upload
   burst-cap-len off
   s" /tmp/gterm-bsrc.tmp" process-burst
   burst-cap s" /tmp/gterm-bsrc.tmp" compare -> 0 }T
T{ enable-drop-upload -> }T

\ --- the whole way through process-keyboard -------------------------------
\ sim-burst-* replays a byte string as one burst: read-key? stays true after
\ every byte but the last, which is exactly how a paste or a drop arrives.

TESTING drag&drop: the path arrives as a burst and the file is uploaded at once
T{ ' sim-burst-key? is read-key?
   ' sim-burst-key  is read-key -> }T
s" /tmp/gterm-bdst2.tmp" w/o create-file throw serial-port !
T{ s" '/tmp/gterm-bsrc.tmp' " feed-burst -> false }T
serial-port @ close-file throw
T{ s" /tmp/gterm-bdst2.tmp" ingest-file  s" BURST-PAYLOAD" compare -> 0 }T

TESTING copy&paste: pasted Forth source still reaches the receiver untouched
T{ s" : half 2/ ; ' half" feed-burst -> false }T
T{ burst-cap s" : half 2/ ; ' half" compare -> 0 }T

T{ saved-key-delay to key-char-delay
   bd-saved-port @ serial-port !
   ' serial-emit is burst-byte
   ' key?  is read-key?
   ' key   is read-key
   ' default-read-serial?  is read-serial?
   ' default-read-serial   is read-serial
   ' default-port-hung-up? is port-hung-up?
   ' default-announce-upload is announce-upload -> }T

TESTING ------ connect-session ------

\ A regular file is not a tty, so the real reset-baud (tcsetattr) throws.
\ This drives default-cleanup-port's error-swallowing path, which must still
\ consume the fid and leave the stack balanced (no leaked cell).
TESTING default-cleanup-port balances the stack when reset-baud errors
T{ s" /tmp/gterm-cleanup.tmp" w/o create-file throw default-cleanup-port -> }T

\ Stub: open a temp file instead of a real serial port.
: stub-open-port ( addr u baud -- fid )
  2drop drop
  s" /tmp/gterm-cs.tmp" w/o create-file throw ;

\ Stub: just close the file (no tty reset).
: stub-cleanup-port ( fid -- )   close-file drop ;

\ ------ the port is kept open across sessions ------
\ Closing the port resets the target, so a clean exit must leave it open and
\ the next session must go on using the very same descriptor.

T{ ' stub-open-port    is open-port
   ' stub-cleanup-port is cleanup-port -> }T

TESTING ensure-port opens a port when none is open
T{ close-port  ensure-port  serial-port @ 0<> -> true true }T

TESTING ensure-port reuses the open port instead of opening a second one
T{ serial-port @  ensure-port  swap serial-port @ = -> false true }T

TESTING a changed device path makes the open port unusable
T{ s" /dev/ttyOTHER" to serial-port-name  port-reusable?
   this-os port-for-os to serial-port-name -> false }T

TESTING a changed baud rate makes the open port unusable
T{ baud-rate  115200 to baud-rate  port-reusable?
   swap to baud-rate -> false }T

TESTING the port is reusable again with the configuration it was opened for
T{ port-reusable? -> true }T

TESTING close-port closes the port and forgets it
T{ close-port  serial-port @  port-reusable? -> 0 false }T

TESTING close-port is harmless when no port is open
T{ close-port  close-port -> }T

TESTING drop-port lets go of a port that errored
T{ ensure-port drop  drop-port  serial-port @  port-reusable? -> 0 false }T

TESTING ------ port locks: devices other instances hold ------
\ Another instance is simulated in-process: a second open file description of
\ the same lock file conflicts with ours exactly as another process's would.
variable other-fid
: other-holds ( dev-a dev-u -- )
  lock-path r/w create-file throw  dup other-fid !
  fileno LOCK_EX LOCK_NB or flock throw ;
: other-releases ( -- )   other-fid @ close-file throw ;

\ Three devices, so a busy one in the middle can be dropped from between two.
: stub-collect-3 ( -- )
  3 device-count !
  s" /dev/ttyA" 0 device[] place
  s" /dev/ttyB" 1 device[] place
  s" /dev/ttyC" 2 device[] place ;

TESTING lock-path maps a device to a lock file under lock-prefix
T{ s" /dev/ttyTEST0" lock-path
   s" /tmp/gterminal-test-_dev_ttyTEST0.lock" compare -> 0 }T

TESTING two names of the same device share one lock file
T{ s" /tmp/gterm-alias-target" w/o create-file throw close-file throw
   s" ln -sf /tmp/gterm-alias-target /tmp/gterm-alias" system
   s" /tmp/gterm-alias" lock-path  pad place
   s" /tmp/gterm-alias-target" lock-path  pad count compare -> 0 }T

TESTING device-busy?: free until another instance holds it, free again after
T{ s" /dev/ttyTEST0" device-busy? -> false }T
T{ s" /dev/ttyTEST0" other-holds  s" /dev/ttyTEST0" device-busy? -> true }T
T{ other-releases  s" /dev/ttyTEST0" device-busy? -> false }T

TESTING take-lock: the device this instance holds does not count as busy
T{ s" /dev/ttyTEST0" take-lock  s" /dev/ttyTEST0" device-busy? -> false }T
T{ s" /dev/ttyTEST0" lock-path lock-held? -> true }T   \ but others see it held

TESTING take-lock for another device lets go of the first
T{ s" /dev/ttyTEST1" take-lock  s" /dev/ttyTEST0" lock-path lock-held? -> false }T
T{ s" /dev/ttyTEST1" lock-path lock-held? -> true }T

TESTING release-lock lets go of the device
T{ release-lock  s" /dev/ttyTEST1" lock-path lock-held? -> false }T
T{ release-lock -> }T                                  \ harmless when none held

TESTING take-lock refuses a device another instance holds
T{ s" /dev/ttyTEST0" other-holds
   s" /dev/ttyTEST0" ' take-lock catch nip nip -> port-in-use-error }T
T{ other-releases -> }T

TESTING drop-busy-devices keeps only the free devices, in order
T{ s" /dev/ttyB" other-holds
   stub-collect-3  drop-busy-devices  device-count @ -> 2 }T
T{ 0 device[] count s" /dev/ttyA" compare -> 0 }T
T{ 1 device[] count s" /dev/ttyC" compare -> 0 }T
T{ other-releases -> }T

TESTING choose-device: the one device left free is taken without asking
\ The noForth duo case: the other interface is open in another instance.
T{ s" /dev/ttyTEST0" other-holds
   ' stub-collect-2 is collect-devices
   ' stub-pick-none is read-choice
   ' 2drop is type  ' noop is cr
   choose-device
   ' (type) is type  ' (cr) is cr
   -> false }T
T{ serial-port-name s" /dev/ttyTEST1" compare -> 0 }T
T{ other-releases -> }T

TESTING choose-device: every device in use gives up (true)
T{ s" /dev/ttyONE" other-holds
   ' stub-collect-1 is collect-devices
   ' 2drop is type  ' noop is cr
   choose-device
   ' (type) is type  ' (cr) is cr
   -> true }T
T{ other-releases
   ' default-collect-devices is collect-devices
   ' default-read-choice     is read-choice
   this-os port-for-os to serial-port-name -> }T

TESTING ensure-port takes the lock for the port it opens
T{ close-port  ensure-port drop
   serial-port-name lock-path lock-held? -> true }T

TESTING close-port lets go of the lock
T{ close-port  serial-port-name lock-path lock-held? -> false }T

TESTING drop-port keeps the lock, so a resetting device is not taken over
T{ ensure-port drop  drop-port
   serial-port-name lock-path lock-held? -> true }T
T{ release-lock -> }T

TESTING ensure-port refuses a port another instance holds, opening nothing
T{ serial-port-name other-holds
   ' ensure-port catch  port-open? -> port-in-use-error false }T
T{ other-releases -> }T

\ ------ an open rides out a device that is re-enumerating ------
\ Closing a port restarts some targets, and one with a native USB interface
\ then re-enumerates: its node is present but refuses to open for a moment
\ (~0.3 s, measured on macOS). A port change closes one port and opens
\ another, so the open lands in that window and must be allowed to retry.

variable op-tries        \ opens attempted
variable op-fails-left   \ opens that are still to fail before one succeeds

: stub-flaky-open ( addr u baud -- fid )
  2drop drop
  1 op-tries +!
  op-fails-left @ 0> if  -1 op-fails-left +!  -37 throw  then
  s" /tmp/gterm-retry.tmp" w/o create-file throw ;

\ Run open-port-retrying under catch, leaving just the result: the throw code,
\ or 0 when it opened (catch has restored the arguments on the error path).
: try-retrying ( -- ior )
  s" /dev/x" 0 ['] open-port-retrying catch ?dup if
    >r drop 2drop r>
  else
    close-file drop 0
  then ;

T{ ' stub-flaky-open is open-port  0 to open-retry-delay-ms -> }T

TESTING an open that succeeds at once is not retried
T{ 0 op-tries !  0 op-fails-left !  try-retrying  op-tries @ -> 0 1 }T

TESTING an open that fails twice and then succeeds is retried until it does
T{ 0 op-tries !  2 op-fails-left !  try-retrying  op-tries @ -> 0 3 }T

TESTING an open that never succeeds rethrows after open-retry-max attempts
T{ 0 op-tries !  open-retry-max 1+ op-fails-left !
   try-retrying  op-tries @ -> -37 open-retry-max }T

TESTING a port change rides out a node that is briefly unopenable
T{ close-port  0 op-tries !  2 op-fails-left !
   s" /dev/ttyRETRY" to serial-port-name
   ensure-port  op-tries @ -> true 3 }T

T{ close-port
   this-os port-for-os to serial-port-name
   ' stub-open-port is open-port
   200 to open-retry-delay-ms -> }T

\ Step counter drives two key events: ESC then ESC.
\ These must model *typing*, not a burst: process-keyboard asks read-key? a
\ second time right after each read-key to see whether more bytes are already
\ waiting, and a stub that always says "ready" would look like a paste — which
\ deliberately does not exit. cs-armed therefore reports a key on one turn of
\ the connect loop and no key on the follow-up question, re-arming for the next
\ turn, so each ESC arrives on its own.
variable cs-step    \ ESC keypresses delivered so far
variable cs-armed   \ true when the next read-key? should report a keypress

: cs-esc-key? ( -- f )
  cs-armed @ if  cs-step @ 2 <  else  cs-armed on  false  then ;
: cs-esc-key  ( -- char )  1 cs-step +!  cs-armed off  27 ;

T{ ' stub-open-port    is open-port
   ' stub-cleanup-port is cleanup-port
   ' cs-esc-key?  is read-key?
   ' cs-esc-key   is read-key
   sim-serial-ready off -> }T

TESTING connect-session: ESC-ESC causes clean exit; catch returns 0
T{ 0 cs-step !  cs-armed on  reset-esc
   ' connect-session catch -> 0 }T

TESTING connect-session sets connected? once the port has opened
T{ 0 cs-step !  cs-armed on  reset-esc  connected? off
   ' connect-session catch drop
   connected? @ -> true }T

TESTING ESC-ESC leaves the port open, so the target keeps its state
T{ close-port  0 cs-step !  cs-armed on  reset-esc
   ' connect-session catch drop
   serial-port @ 0<> -> true }T

TESTING ESC-ESC keeps the lock too, so no other instance takes the port
T{ serial-port-name lock-path lock-held? -> true }T

TESTING the next connect-session goes on using that same port
T{ serial-port @  0 cs-step !  cs-armed on  reset-esc
   ' connect-session catch drop
   serial-port @ = -> true }T

\ Only a fresh port gets the CR that fetches a prompt; counting the CRs the
\ session sends to the port tells a fresh open from a reuse. The ESC keys the
\ stub types are 27, so they never count.
variable cs-cr-count
: cs-count-cr ( char port -- )   drop 13 = if 1 cs-cr-count +! then ;

TESTING connect-session nudges a freshly opened port with CR
T{ ' cs-count-cr is process-input
   close-port  0 cs-cr-count !
   0 cs-step !  cs-armed on  reset-esc
   ' connect-session catch drop
   cs-cr-count @ -> 1 }T

TESTING connect-session sends nothing to a port it merely reuses
T{ 0 cs-cr-count !
   0 cs-step !  cs-armed on  reset-esc
   ' connect-session catch drop
   cs-cr-count @ -> 0 }T

\ Hand the keyboard path back the way a live session needs it: enable-drop-upload
\ sets process-input *and* process-burst, so restoring only serial-emit would
\ leave the typed path without smart-process-input — and with it without del>bs.
T{ enable-drop-upload
   close-port -> }T

T{ ' open-serial-nohup    is open-port
   ' default-cleanup-port is cleanup-port
   ' key?  is read-key?
   ' key   is read-key -> }T

\ ------ connect ------

variable run-count
variable throws-left

\ Stub: connection established (sets connected?), then throws a device error.
: stub-run-session ( -- )
  1 run-count +!
  connected? on
  throws-left @ 0> if -1 throws-left +!  -1 throw then ;

\ Stub: initial open fails before a connection is established.
: stub-open-fail ( -- )
  1 run-count +!  -1 throw ;

\ Stub connect-failed: request a retry while fail-retries remain, else give up.
variable fail-retries
: stub-connect-failed ( -- give-up? )
  fail-retries @ 0> if -1 fail-retries +!  false else true then ;

\ Stub disconnected: give up immediately instead of retrying.
: stub-disconnected ( -- give-up? )  true ;

TESTING connect exits after a single clean run
T{
   ' 2drop is type  ' noop is cr
   0 run-count !  0 throws-left !
   0 serial-port !  0 to reconnect-delay-ms
   ' stub-run-session is run-session
   ' connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 1 }T


TESTING connect retries once after an error then exits on clean run
T{
   ' 2drop is type  ' noop is cr
   0 run-count !  1 throws-left !
   0 serial-port !
   ' connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 2 }T


TESTING connect gives up after a failed initial open (no reconnect loop)
T{
   ' 2drop is type  ' noop is cr
   0 run-count !
   0 serial-port !
   ' stub-open-fail is run-session
   ' connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 1 }T


TESTING connect lets go of the lock when it gives up
T{ serial-port-name take-lock
   ' 2drop is type  ' noop is cr
   0 run-count !
   0 serial-port !
   ' stub-open-fail is run-session
   ' connect catch
   ' (type) is type  ' (cr) is cr
   throw
   serial-port-name lock-path lock-held? -> false }T


TESTING connect-failed controls retry: override that retries once then stops
T{
   ' 2drop is type  ' noop is cr
   0 run-count !  1 fail-retries !
   0 serial-port !
   ' stub-open-fail     is run-session
   ' stub-connect-failed is connect-failed
   ' connect catch
   ' (type) is type  ' (cr) is cr
   ' default-connect-failed is connect-failed
   throw
   run-count @ -> 2 }T


TESTING disconnected override gives up instead of retrying
T{
   ' 2drop is type  ' noop is cr
   0 run-count !  9 throws-left !       \ would loop if it kept retrying
   0 serial-port !
   ' stub-run-session  is run-session
   ' stub-disconnected is disconnected
   ' connect catch
   ' (type) is type  ' (cr) is cr
   ' default-disconnected is disconnected
   throw
   run-count @ -> 1 }T


TESTING select-connect: a single device is picked without asking, then connected
T{
   ' 2drop is type  ' noop is cr
   0 run-count !  0 throws-left !
   0 serial-port !
   ' stub-collect-1   is collect-devices
   ' stub-pick-none   is read-choice     \ would give up if it were consulted
   ' stub-run-session is run-session
   ' select-connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 1 }T
T{ serial-port-name s" /dev/ttyONE" compare -> 0 }T

TESTING select-connect: several devices, the chosen one is connected
T{
   ' 2drop is type  ' noop is cr
   0 run-count !
   ' stub-collect-2 is collect-devices
   ' stub-pick-2    is read-choice
   ' select-connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 1 }T
T{ serial-port-name s" /dev/ttyTEST1" compare -> 0 }T

TESTING select-connect: no device found, no session is started
T{
   ' 2drop is type  ' noop is cr
   0 run-count !
   ' stub-collect-0 is collect-devices
   ' select-connect catch
   ' (type) is type  ' (cr) is cr
   throw
   run-count @ -> 0 }T

T{ ' default-collect-devices is collect-devices
   ' default-read-choice     is read-choice
   this-os port-for-os to serial-port-name -> }T

T{ ' connect-session is run-session
   1000 to reconnect-delay-ms -> }T


TESTING ------ version and banner ------

TESTING the version is the released 1.0.0
T{ gterminal-version s" 1.0.0" compare -> 0 }T

TESTING the boot banner names the program and its version
T{ ' .banner >string-execute  s" gterminal 1.0.0 " string-prefix? -> true }T
T{ ' .banner >string-execute  s" press ESC twice" search nip nip -> true }T


TESTING ------ every borrowed hook is handed back ------

\ The tests stub the deferred words and restore them afterwards. Restoring the
\ wrong one leaves a hook disabled for every real session, and nothing else
\ notices: that is how process-input came to be serial-emit instead of
\ smart-process-input, taking del>bs (DEL -> BS) out of the typed path. So the
\ last thing the suite does is check the bindings a live session runs on.
\ connect-failed and disconnected are deliberately absent: they are pointed at
\ the device chooser below, after the tests.

TESTING the keyboard path is bound for a live session
T{ ' process-input defer@ ' smart-process-input = -> true }T   \ del>bs lives here
T{ ' process-burst defer@ ' dispatch-burst      = -> true }T
T{ ' burst-byte    defer@ ' serial-emit         = -> true }T
T{ ' key-xlat      defer@ ' lf>eol              = -> true }T
T{ ' read-key      defer@ ' key                 = -> true }T
T{ ' read-key?     defer@ ' key?                = -> true }T

TESTING the serial path is bound for a live session
T{ ' process-output  defer@ ' emit                  = -> true }T
T{ ' read-serial     defer@ ' default-read-serial   = -> true }T
T{ ' read-serial?    defer@ ' default-read-serial?  = -> true }T
T{ ' port-hung-up?   defer@ ' default-port-hung-up? = -> true }T

TESTING the port lifecycle is bound for a live session
T{ ' open-port    defer@ ' open-serial-nohup    = -> true }T
T{ ' cleanup-port defer@ ' default-cleanup-port = -> true }T
T{ ' run-session  defer@ ' connect-session      = -> true }T

TESTING device selection and upload are bound for a live session
T{ ' collect-devices defer@ ' default-collect-devices = -> true }T
T{ ' read-choice     defer@ ' default-read-choice     = -> true }T
T{ ' upload-copy     defer@ ' copy-file-to            = -> true }T
T{ ' upload-emit     defer@ ' default-upload-emit     = -> true }T
T{ ' upload-ticks    defer@ ' default-upload-ticks    = -> true }T
T{ ' announce-upload defer@ ' default-announce-upload = -> true }T

TESTING the suite leaves no lock held and locks back in their real place
T{ release-lock  default-lock-prefix to lock-prefix -> }T
T{ lock-fid @  held-lock nip -> 0 0 }T

[then]

\ When a connect fails, list the present serial devices and let the user pick.
\ On a drop, reconnect-or-choose retries the current port once first, so a
\ successful reconnect stays quiet; only a failed reopen shows the menu.
' choose-device       is connect-failed
' reconnect-or-choose is disconnected

warnings on
.banner

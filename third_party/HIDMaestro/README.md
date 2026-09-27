# HIDMaestro SDK 1.9.0

Unmodified official `HIDMaestro.Core.dll` from the x64 release archive:
https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.9.0/HIDMaestro-v1.9.0.zip

- Release tag: `v1.9.0`, commit `942e25a`.
- DLL SHA-256: `D613BE086178D34DEF0C8D3869E801B55CE16D49B7A6E4516281D067AA730D9A`.
- Downloaded ZIP SHA-256: `1FA4A57B6F2DB9DC943BB81047808FDF097955497B96F22C1944DDD961B59605`.
- DLL size: 67,623,424 bytes; build verifies its full hash.
- Target: `net10.0-windows10.0.26100.0`; app packages Windows x64.
- MIT license is preserved verbatim in `LICENSE`; embedded third-party notices are also supplied.

No driver is installed by building. Installation is a separate elevated UI action.
The SDK installation installs a local signing certificate and UMDF driver packages,
and may remove existing HIDMaestro devices; the application refuses it when any
present HIDMaestro controller is found. Creation also refuses concurrent consumers
because the SDK allocates shared-memory controller indices per process.

Protocol references (original adapter; no PadForge source copied):

- https://github.com/hifihedgehog/HIDMaestro/blob/v1.9.0/profiles/microsoft/xbox-series-xs-bt.json
- https://github.com/hifihedgehog/HIDMaestro/blob/v1.9.0/sdk/HIDMaestro.Core/HMOutputPacket.cs
- https://github.com/hifihedgehog/HIDMaestro/blob/v1.9.0/test/Program.cs
- https://github.com/quantus/xbox-one-controller-protocol

The pinned profile has no HID report IDs. Its output is eight bytes: motor
enable bits (LT=8, RT=4, large=2, small=1), four 0..100 magnitudes, duration,
delay in 10 ms units, and repeat count. The pinned driver's output handlers
unconditionally split the first byte into `HMOutputPacket.ReportId`, so normal
feedback has the motor mask in ReportId and seven bytes of data. A zero-prefixed
eight-byte data representation is also accepted with ReportId zero. Raw fields
are retained exactly as received. See the v1.9.0 `driver/driver.c` output handlers.
XInput is decoded only for body rumble and never interpreted as impulse triggers.

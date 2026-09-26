# ForzaHaptics

**Forza Horizon 6** Data Out telemetry (UDP) translated into native **DualSense haptics**.

ForzaHaptics receives telemetry from the game, separates it into effects such as road texture, rumble strips, potholes, slides, wheel lock, and collisions, then synthesizes an audio signal that is sent directly to the controller's left and right haptic actuators.

DSX and DualSenseY are not required: DualSense haptics are driven by an ordinary audio signal, which ForzaHaptics writes directly to the controller.

```text
FH6 ──UDP, 324 bytes per frame──▶ TelemetryProcessor ──▶ HapticBus ──▶ HapticSynth ──▶ USB: WASAPI, channels 3–4 (48 kHz)
                                      (60–144 Hz: effect   (targets +     (48 kHz / 3 kHz,   BT: HID report 0x32 (3 kHz, experimental)
                                       levels + impulses)  queue)         smooth interpolation)
```

## What you feel

| Effect | Telemetry source | Haptic character |
|---|---|---|
| Road texture | `SurfaceRumble` + speed | 70–190 Hz noise that grows with speed, calculated separately for the left and right sides |
| Rumble strips | `WheelOnRumbleStrip` | Pulses at a frequency derived from *speed / rumble-strip spacing* |
| Potholes, joints, and landings | Suspension travel velocity (`SuspensionTravelMeters`) | An 85 Hz thump whose strength follows compression velocity |
| Oversteer / understeer | `TireCombinedSlip` > 1 | Irregular scraping |
| Wheelspin | `TireSlipRatio` under throttle | 150 Hz buzzing |
| Wheel lock | `TireSlipRatio` under braking | 14 Hz pulses on a 150 Hz carrier, similar to ABS |
| Engine and rev limiter | RPM and throttle | A subtle 55–190 Hz tone with stuttering at the limiter |
| Gear shifts | Changes in `Gear` | A short kick |
| Collisions | Horizontal G-force peaks and `SmashableVelDiff` | A strong impact on the side from which the collision came |
| Puddles | `WheelInPuddle` | An entry splash followed by viscous noise |

### L2/R2 adaptive triggers

The trigger logic is based on [HorizonHaptics](https://github.com/haritha99ch/HorizonHaptics). Its settings are available in the **Triggers** section of the application's left navigation, under the `"Triggers"` section of the profile file.

| Trigger | Condition | Feedback |
|---|---|---|
| L2 | Braking | Resistance from 0 to 7, increasing with pedal pressure |
| L2 | Handbrake | A hard wall |
| L2 | ABS (slip while braking) | Springy upper zones (`AbsWallStrength`) and lower zones pulsing at 20–40 Hz |
| R2 | Acceleration | Light resistance from acceleration, with an additional turbo contribution |
| R2 | Wheelspin / sliding under throttle | A soft 30–60 Hz buzz that ramps up over 200 ms; strength is controlled by `VibAmpMin..VibAmpMax` |
| Both | Gear shift or impact with an object | A short impulse |
| Both | Trigger released | Rumble strips and road texture |

`"Mode": "Resistance"` provides resistance without vibration. `"Off"` leaves the trigger free.

`"Strength"` (default: 0.65) scales all trigger effects together to reduce load on the mechanism. `"Hysteresis"` makes the exit threshold lower than the entry threshold and prevents resistance and vibration frequency from jumping in response to tiny fluctuations, so the trigger does not chatter around a boundary. `--render` reports how many times the trigger state changed; every change writes a new state to the controller.

## Requirements

- Windows 10 or Windows 11 and the **.NET 8 SDK** or newer. Visual Studio 2022 with the **.NET desktop development** workload already includes it.
- A DualSense or DualSense Edge controller:
  - **USB is recommended.** The controller appears as a four-channel sound card: channels 1–2 drive the speaker/headphones, while **channels 3–4 drive the haptics**.
  - **Bluetooth support is experimental** and uses the SAxense protocol described below.
- NuGet restores [Avalonia Desktop 12.1.3](https://www.nuget.org/packages/Avalonia.Desktop), [CommunityToolkit.Mvvm 8.4.2](https://www.nuget.org/packages/CommunityToolkit.Mvvm), `NAudio.Wasapi 2.2.1`, and `HidSharp 2.1.0` automatically.

The UI is built with Avalonia and an MVVM architecture. Avalonia makes the presentation layer suitable for future cross-platform work, but the application currently remains **Windows-only**: the USB output uses Windows WASAPI, and several system integrations are Windows-specific. Linux and macOS audio/HID backends are not implemented yet.

## Quick start

1. Open `ForzaHaptics.sln` in Visual Studio.
2. Connect the DualSense over USB. Open **Control Panel → Sound → Playback → DualSense Wireless Controller**, then:
   - Select **Configure → Quadraphonic**. Without this, Windows exposes only two channels and haptics are unavailable.
   - Set **Properties → Levels → 100%**. Device volume also controls haptic strength.
   - Do **not** make the DualSense the default playback device, or game audio will be routed to the controller.
3. Start the application with F5 or `ForzaHaptics.exe`. The window opens and controller output starts automatically. Select **Motor test**: the left actuator should vibrate first, followed by the right. If they are reversed, enable **Swap left/right** in the **General** section of the left navigation.
4. Enable **Simulation** to run a 42-second synthetic drive containing rumble strips, gravel, a jump, a slide, a collision, and more. This lets you feel every effect without launching the game; the current phase appears in the status bar.
5. In FH6, open **Settings → HUD and Gameplay** and set **Data Out** to **On**, **Data Out IP Address** to `127.0.0.1`, and **Data Out IP Port** to `5310`. Disable **Simulation** and drive.

## Window and profiles

- The Avalonia interface uses a fixed dark theme with high-contrast text and controls, independently of the Windows theme or accent color.
- The top **DualSense** panel monitors the controller even when haptics are stopped. It shows USB/Bluetooth connection, an approximate battery percentage, and a lightning symbol while charging. The ring is red at 20% or below, amber through 50%, and green above 50%; `—` means the battery reading is unavailable. DualSense reports charge in coarse steps, not precise one-percent measurements.
- The **power** button stops haptics and adaptive triggers, then disconnects the displayed Bluetooth controller without confirmation. Reconnect it manually and select **Start** to resume. USB supplies power, so the button is disabled over USB. Bluetooth disconnect and physical power-off have been verified on a DualSense; the Windows command itself requests a disconnect.
- The application uses the hybrid Forza × DualSense icon; its SVG/PNG/ICO exports and size preview are in [design/icons](design/icons/README.md).
- Bluetooth battery readings require enhanced input reports. Monitoring is passive: it does not change the controller's report mode merely to obtain a battery reading. Until enhanced reports are available, the panel shows `—`. Forcing that mode can affect Windows DirectInput compatibility; see the [SDL enhanced-report documentation](https://wiki.libsdl.org/SDL3/SDL_HINT_JOYSTICK_ENHANCED_REPORTS).
- The **General**, **Vibration**, and **Triggers** sections are available from the left navigation. Hover over a setting name to see its description. Changes are **applied immediately**, so you can feel them while tuning, but they are written to disk only when you select **Save** or press Ctrl+S. **Revert** restores the saved values, and unsaved changes add `*` to the window title.
- Fields marked with **⟳**—including port, output, channels, and trigger enablement—take effect after **Restart output**.
- Runtime status, device information, output levels, and engine controls are grouped below the settings. The **Activity log** is collapsed by default; expand it when you need detailed runtime messages.
- Profiles are `*.json` files in the **`Configs` directory beside the executable**. You can create a profile from defaults with **New**, or **Duplicate**, **Rename**, **Delete** it to the Recycle Bin, and switch profiles using the **Profile** list. The most recently selected profile is restored at the next launch through `ForzaHaptics.settings.json`, also beside the executable.
- Files in `Configs` are discovered at startup and when you select **⟳ Refresh**, so a profile can be installed by simply copying it into the directory.
- On first launch, an existing `config.json` is migrated to a profile named `Default`, with comments preserved. Profiles may also be edited by hand; file changes are reloaded while the application is running.

## Command line

Console modes remain available. Use `dotnet run --project ForzaHaptics -- --simulate` (application arguments follow `--`) or run `ForzaHaptics.exe --console` from a terminal. Settings come from the active profile unless `--config FILE` is supplied.

```text
ForzaHaptics [options]
  (no options)        open the settings and profiles window
  --console           run without a window: receive FH6 telemetry and drive haptics
  --simulate          use the synthetic drive instead of the game
  --test              test the haptic actuators
  --record FILE       record telemetry to a file while playing
  --replay FILE       replay a recording in a loop for tuning without the game
  --render FILE.wav   render offline to WAV without a controller (simulation or --replay)
  --list              list audio devices and DualSense HID devices
  --output auto|usb|bt, --port N, --config FILE
```

## Configuration and tuning

All settings are available in the window and in the corresponding profile file under `Configs`. **Gain** controls effect strength; **Enabled** turns an effect on or off.

Tuning tips:

- The status bar displays `surface 0.xx`, the raw `SurfaceRumble` value from the game. Drive on asphalt and dirt, compare the values, then adjust `Road.SurfaceScale` so dirt feels noticeably stronger than asphalt.
- `L 0.xx R 0.xx` shows peak actuator levels. Adjust overall strength with `Dynamics.Makeup` or `MasterGain`. Peaks near 1.0 are normal: the output stage includes a compressor and soft limiter, so impacts are not hard-clipped.
- Record a drive with `--record lap.fhrec`, then tune against `--replay lap.fhrec` without playing. `--render lap.wav --replay lap.fhrec` produces a WAV whose left and right channels correspond to the two actuators, making the signal easy to inspect in Audacity.
- DualSense uses voice-coil actuators rather than eccentric rotating masses. Frequencies below roughly 60 Hz are barely perceptible, the strongest range is about 100–250 Hz, and frequencies above 350 Hz begin to squeal. Effect carriers therefore sit in the useful range, while low-frequency rhythms such as rumble strips and ABS modulate their amplitude. The output is constrained by the `LowCutHz..HighCutHz` band-pass filter.
- `--test` sweeps from 20 to 400 Hz. Note where vibration feels strongest and, if necessary, move effect `FreqHz` values toward that range.

## Compatibility and troubleshooting

- **DSX / DualSenseY:** Disable *Audio to Haptics* or *Audio passthrough* to prevent mixed signals over USB or conflicts over Bluetooth. Gamepad emulation and adaptive triggers may remain enabled.
- **The triggers do not respond:** Steam Input, DS4Windows, or DSX may overwrite trigger effects; disable trigger effects in those applications. If ForzaHaptics cannot open the HID device, it displays a warning and continues with haptics only.
- **The game's own vibration:** Steam Input can emulate rumble on the DualSense actuators. If the combined effects become muddy, disable vibration in the game or in Steam's controller settings.
- **Only one application can listen on a UDP port:** To run another telemetry application in parallel, such as a trigger tool or SimHub, configure it on a different port and add `"ForwardTo": ["127.0.0.1:5300"]`. ForzaHaptics will forward the packets.
- **No telemetry packets arrive:** Verify the port and IP address in the game. With an Xbox app / Game Pass build, Windows may prevent UWP applications from sending to `127.0.0.1`. If so, open PowerShell as Administrator, run `Get-AppxPackage *forza*`, copy the `PackageFamilyName`, then run `CheckNetIsolation LoopbackExempt -a -n="<PackageFamilyName>"`.
- **Bluetooth:** Bluetooth does not expose an audio endpoint, so 8-bit, 3 kHz PCM is packed into HID `0x32` reports approximately every 10.7 ms. The controller must be in its "full" mode, which Steam Input, DSX, or DSY enables while connected. If Bluetooth output does not work, use USB.

## Architecture

```text
ForzaHaptics/
  Telemetry/                       FH6 packet parsing, UDP receive/forwarding, recording, replay, and simulation
  Haptics/                         telemetry processing, DSP, impulses, and two-actuator signal synthesis
  Output/                          WASAPI, Bluetooth HID, trigger HID, and WAV output
  Controllers/                     independent HID presence/battery monitoring and Windows Bluetooth disconnect
  Triggers/                        telemetry-driven L2/R2 processing and trigger-effect encoding
  Config/                          profile storage, load/save, hot reload, and live configuration updates
  Gui/App.axaml(.cs)               Avalonia application and Fluent theme bootstrap
  Gui/MainWindow.axaml(.cs)        main Avalonia window and view-specific lifecycle integration
  Gui/Views/SettingsView.axaml     settings editor generated from [Ui] metadata
  Gui/ViewModels/                  MVVM presentation state and commands
  Gui/Services/ and Gui/Dialogs/   platform integrations and asynchronous modal dialogs
  AppConfig.cs                     settings and UI field metadata ([Ui], [UiGroup])
  HapticEngine.cs                  real-time engine shared by GUI and console modes
  config.json                      defaults used to create the Default profile
  Program.cs                       entry point, console modes, and offline rendering
```

The presentation layer uses **Avalonia 12.1.3** with compiled bindings and **CommunityToolkit.Mvvm 8.4.2**. View models own UI state and commands; code-behind is limited to view-specific lifecycle work. The haptic engine and console modes remain independent of the UI framework.

To add an effect, calculate its level in `TelemetryProcessor.Process`, place it in `HapticTargets` or enqueue a `HapticKick`, then add the corresponding generator in `HapticSynth.Render`.

## Validation

Run `dotnet run --project ForzaHaptics.Tests` for parser, controller lifecycle, view-model, and Avalonia layout tests. Set `FORZAHAPTICS_SCREENSHOT_DIR` to an output folder to render Bluetooth, charging USB, and disconnected states at two window widths and 100%/150%/200% scale.

Hardware diagnostics are opt-in. Set `FORZAHAPTICS_HARDWARE_TEST=1` and `FORZAHAPTICS_HARDWARE_MODE` to `passive`, `haptics`, or `disconnect`, then run `dotnet run --project ForzaHaptics.Tests -- -class ForzaHaptics.Tests.ControllerHardwareTests`. Passive mode only reads status; haptics runs a three-second motor test; disconnect removes the selected Bluetooth connection. Ordinary tests do not access a physical controller.

## Acknowledgements

- Bluetooth haptics protocol: **SAxense** by Sdore — <https://apps.sdore.me/SAxense> and <https://github.com/egormanga/SAxense>. The author asks projects to reference the underlying research, so both links are included here. This repository contains an independent C# implementation of the protocol.
- Telemetry format: [Forza Horizon 6 “Data Out” Documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation).
- Ideas and channel verification: [DualSenseY-v2](https://github.com/WujekFoliarz/DualSenseY-v2) and [HorizonHaptics](https://github.com/haritha99ch/HorizonHaptics).
- Libraries: [Avalonia](https://avaloniaui.net/), [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/), [NAudio](https://github.com/naudio/NAudio) (MIT), and [HidSharp](https://www.zer7.com/software/hidsharp) (Apache 2.0).

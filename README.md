# ForzaHaptics

**Forza Horizon 6** Data Out telemetry (UDP) translated into native **DualSense haptics**.

ForzaHaptics receives telemetry from the game, separates it into effects such as road texture, rumble strips, potholes, slides, wheel lock, and collisions, then synthesizes an audio signal that is sent directly to the controller's left and right haptic actuators.

DSX and DualSenseY are not required: DualSense haptics are driven by an ordinary audio signal, which ForzaHaptics writes directly to the controller.

```text
FH6 ──UDP, 324 bytes per frame──▶ TelemetryProcessor ──▶ HapticBus ──▶ HapticSynth ──▶ USB: WASAPI, channels 3–4 (48 kHz)
                                      (game FPS: effect   (targets +     (48 kHz / 3 kHz,   BT: HID report 0x32 (3 kHz, experimental)
                                       levels + impulses)  queue)         smooth interpolation)
```

## What you feel

| Effect | Telemetry source | Haptic character |
|---|---|---|
| Road texture | `SurfaceRumble` + speed | 70–190 Hz noise that grows with speed, calculated separately for the left and right sides |
| Rumble strips | `WheelOnRumbleStrip` | Pulses at a frequency derived from *speed / rumble-strip spacing* |
| Potholes, joints, and landings | Suspension travel velocity (`SuspensionTravelMeters`) | An 85 Hz thump whose strength follows compression velocity |
| Combined grip loss | `TireCombinedSlip` > 1 | Irregular scraping |
| Wheelspin | `TireSlipRatio` under throttle | 150 Hz buzzing |
| Wheel lock | `TireSlipRatio` under braking | 14 Hz pulses on a 150 Hz carrier, similar to ABS |
| Engine and rev limiter | RPM and throttle | A subtle 55–190 Hz tone with stuttering at the limiter |
| Gear shifts | Changes in `Gear` | A short kick |
| Collisions | Horizontal G-force peaks and `SmashableVelDiff` | A strong impact on the side from which the collision came |
| Puddles | `WheelInPuddle` | An entry splash followed by viscous noise |

### L2/R2 adaptive triggers

The **Triggers** tab controls vibration events. There is no background resistance curve: triggers are free between events and immediately released when the physical pedal is released.

| Trigger/effect | Factory behavior before intensity/scaling |
|---|---|
| Overall | Global strength 0.65; each channel intensity 0.7 |
| Gear shift | R2 up/down; L2 down only; 20 Hz, amplitude 1.0, 300 ms with 50 ms attack/release |
| R2 slip | Driven-wheel longitudinal slip plus inferred lateral slip; 30–45 Hz, amplitude 0.35–0.5 |
| L2 slip | Inferred braking slip; 25–35 Hz, amplitude 0.35–0.5 |
| Slip pattern | Continuous by default; optional repeated 250 ms bursts / 250 ms gaps, 80 ms attack/release |
| Optional road/collision | Disabled in new profiles; can be enabled independently |

Cues require physical HID travel above 20/255 and stop at 10/255 or less. Releasing a trigger cancels its cue and sends Off. A pause, stale telemetry/input (300 ms), disconnected/wrong device or Stop disables both triggers. Longitudinal slip uses drivetrain-aware TireSlipRatio; lateral slip uses TireSlipAngle and is an inference, not a confirmed oversteer state. Braking suppresses R2 wheelspin but not lateral slip.

Gear changes have priority over collision, slip and road cues. A 50 ms transition fades the old effect to zero; the shift envelope starts afterward; the trigger returns to Off or the currently eligible lower-priority vibration. Pending shifts older than 150 ms are discarded and new shifts never extend an active pulse. Production trigger output uses only Off and Vibration. Repeated-slip gaps use zero vibration output.

Physical input and accepted telemetry feed one 100 Hz trigger clock. Changed output reports are coalesced to at most 20 Hz; duplicate reports are suppressed. Emergency Off bypasses the interval. Vibration frequency, report rate and mechanical cycles are different quantities. Strength remains an editable multiplier, not a certified force or lifetime limit.

Old profiles migrate in memory to **version 4**. Resistance fields are removed regardless of their old enabled state. Slip and event permissions, amplitudes and frequencies are preserved. Old explicit events are no longer blocked by the former Resistance mode. Save explicitly to write the upgraded JSON; build/publish never overwrites an existing profile.

**Gear trigger test** (GUI or `--trigger-test`) is separate from Motor test: for 12 seconds the current profile starts with free triggers, then upshifts at 2/6/10 seconds and downshifts at 4/8 seconds, then releases both triggers. No slip, road or collision signals are generated. Leave each channel master enabled and select the desired gear directions. For gear-only driving, disable Wheelspin, Lateral slip and Braking slip separately; these switches never disable gear cues. Hold the relevant trigger to feel cues; body output remains silent. The display distinguishes desired effects, successful OS HID writes, and firmware-reported mode. The latter is not a physical force measurement. Offline WAV rendering has no physical input and does not simulate trigger events.

## Requirements

- Windows 11 24H2 or newer, x64, and the **.NET 10 SDK** to build (Windows SDK target 10.0.26100.0). Framework-dependent builds require the .NET 10 runtime; the published self-contained Windows x64 build includes its runtime.
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
3. Start the application with F5 or `ForzaHaptics.exe`. With **Auto-start** enabled (the default), the window opens and starts listening on the UDP port, even without a controller. Haptics start when a controller is available. Select **Motor test**: the left actuator should vibrate first, followed by the right. If they are reversed, enable **Swap left/right** in the **General** section of the left navigation.
4. Enable **Simulation** to run a 42-second synthetic drive containing rumble strips, gravel, a jump, a slide, a collision, and more. This lets you feel every effect without launching the game; the current phase appears in the status bar.
5. In FH6, open **Settings → HUD and Gameplay** and set **Data Out** to **On**, **Data Out IP Address** to `127.0.0.1`, and **Data Out IP Port** to `5310`. Disable **Simulation** and drive.

## Window and profiles

- The Avalonia interface uses a fixed dark theme with high-contrast text and controls, independently of the Windows theme or accent color.
- The top **DualSense** panel monitors the controller even when haptics are stopped. It shows USB/Bluetooth connection, an approximate battery percentage, and a lightning symbol while charging. The ring is red at 20% or below, amber through 50%, and green above 50%; `—` means the battery reading is unavailable. DualSense reports charge in coarse steps, not precise one-percent measurements.
- The **power** button releases haptics and adaptive triggers, then disconnects the displayed Bluetooth controller without confirmation. UDP listening, forwarding, and recording continue. Reconnect the controller to resume effects automatically; **Output: auto** also supports reconnecting over USB after Bluetooth disconnects. USB supplies power, so the button is disabled over USB. Bluetooth disconnect and physical power-off have been verified on a DualSense; the Windows command itself requests a disconnect.
- **Auto-start**, in the Start/Stop control, remembers whether to listen on the UDP port at the next app launch. It is enabled by default and saved immediately in `ForzaHaptics.settings.json`, independently of profiles (including Default). Toggling it does not start or stop the current session. **Stop** stops the session and automatic output reconnection until you select **Start** again. Controller disconnections leave an active session listening and waiting for the controller.
- The application uses the hybrid Forza × DualSense icon; its SVG/PNG/ICO exports and size preview are in [design/icons](design/icons/README.md).
- Bluetooth battery readings require enhanced input reports. Monitoring is passive: it does not change the controller's report mode merely to obtain a battery reading. Until enhanced reports are available, the panel shows `—`. Forcing that mode can affect Windows DirectInput compatibility; see the [SDL enhanced-report documentation](https://wiki.libsdl.org/SDL3/SDL_HINT_JOYSTICK_ENHANCED_REPORTS).
- The **General**, **Vibration**, and **Triggers** sections are available from the left navigation. Hover over a setting name to see its description. Changes are **applied immediately**, so you can feel them while tuning, but they are written to disk only when you select **Save** or press Ctrl+S. **Revert** restores the saved values, and unsaved changes add `*` to the window title.
- Fields marked with **⟳**—including port, output, channels, and trigger enablement—take effect after **Restart output**.
- Runtime status, device information, output levels, and engine controls are grouped below the settings. The **Activity log** is collapsed by default; expand it when you need detailed runtime messages.
- Profiles are `*.json` files in the **`Configs` directory beside the executable**. You can create a profile from defaults with **New**, or **Duplicate**, **Rename**, **Delete** it to the Recycle Bin, and switch profiles using the **Profile** list. The most recently selected profile is restored at the next launch through `ForzaHaptics.settings.json`, also beside the executable.
- Files in `Configs` are discovered at startup and when you select **⟳ Refresh**, so a profile can be installed by simply copying it into the directory.
- **Default** is built into the application and read-only. Select it to inspect or use the factory settings; use **Duplicate** to make an editable copy. It cannot be edited, saved, renamed, or deleted, and it has no JSON file.
- A clean installation starts with **profile_1**, stored as `Configs/profile_1.json`. Its commented template is embedded in the executable and written only when missing. Builds and publishing never overwrite your runtime profile. Editable profiles reload after external edits.
- Root-level `config.json` is no longer discovered or imported automatically. An existing `Configs/Default.json` is preserved by migration to `profile_1`, or a unique `Default_imported_N` when that name is occupied; the saved selection follows the migrated profile.
- The last selection is restored at startup. If it is missing or invalid, the app logs the problem and tries `profile_1`, then built-in Default. Explicit `--config FILE` remains supported and never changes built-in Default.

## Xbox emulation and Impulse Triggers

The **Xbox emulation** switch creates one virtual Xbox controller from the active DualSense or DualSense Edge. It works independently of Forza telemetry: use Xbox input alone, telemetry haptics alone, or both together. **Enable Xbox emulation on startup** is a separate, immediately saved application preference, disabled by default. Start/Stop controls telemetry, not emulation. The physical controller panel continues to show its real battery and USB/Bluetooth connection; the Xbox row shows virtual-device status.

For the default **Xbox 360 — ViGEm** backend, install both [HidHide](https://github.com/nefarius/HidHide/releases) and [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) from their official releases. The app checks their APIs and offers download links and **Check again** when setup is incomplete. Installation is manual; restart Windows if the installer requests it. ViGEmBus is a retired dependency: see its [end-of-life statement](https://docs.nefarius.at/projects/ViGEm/End-of-Life/). NuGet supplies the pinned `Nefarius.ViGEm.Client 1.21.256` client library, not the kernel driver.

Enable emulation before launching the game. ForzaHaptics permits its own executable in HidHide, creates the Xbox device, and hides only the selected physical HID controller. It does not hide the USB sound device. Games with an already-open controller handle may need to be restarted or the controller reconnected for hiding to take effect. Disable other Xbox emulators such as Steam Input, DSX or DS4Windows for this controller to avoid duplicate virtual devices. HidHide's inverted application list is not supported; the error explains how to correct it. If HidHide is inactive with other devices already in its blacklist, configure/enable that list in HidHide first so enabling our controller does not silently hide unrelated devices.

Buttons map by position: Cross/Circle/Square/Triangle to A/B/X/Y, L1/R1 to LB/RB, Create/Options to Back/Start, and PS to Guide. Sticks, stick clicks, D-pad and L2/R2 map to their Xbox equivalents. No additional deadzone or response curve is applied. Touchpad, gyro, microphone mute and extra Edge buttons have no separate Xbox mapping. Basic and enhanced Bluetooth input are supported, including transitions when output enables enhanced reports; basic reports have no battery or trigger-effect feedback. Bluetooth enhanced mode can persist until the controller reconnects.

Game vibration is forwarded as standard DualSense HID rumble over USB or Bluetooth and does not require a four-channel audio endpoint. A running telemetry session, including waiting for packets, simulation, replay and motor/trigger tests, has absolute priority: Xbox rumble is stopped and incoming rumble commands are discarded. After Stop, only new game-rumble commands take effect. Existing experimental Bluetooth PCM limitations still apply to telemetry haptics. The ViGEm Xbox 360 backend provides ordinary two-motor rumble only. **Xbox Series — HIDMaestro** also receives independent game LT/RT Impulse Trigger commands and maps them to DualSense L2/R2 adaptive vibration (15 Hz, start zone 1, amplitude 1–8). Explicit zero turns off the corresponding trigger. Body vibration is not treated as Impulse Triggers. Successful OS writes confirm delivery to the HID API, not measured physical vibration.

Disconnected controllers remove the virtual Xbox; reconnection restores it while the switch remains enabled. Input older than 300 ms releases virtual buttons/axes and stops rumble. Turning emulation off or closing the app removes the virtual device and reverses only the HidHide entries added by this app. Preexisting hidden-device entries remain in place. Only one ForzaHaptics instance can manage emulation at a time.

HidHide changes are journaled to `%LOCALAPPDATA%\ForzaHaptics\hidhide-recovery.json` before application and recovered before the next startup attempt. After a crash, the physical controller can remain hidden until ForzaHaptics is reopened. If recovery fails, close other HidHide configuration tools and select **Check again**. To recover manually, open **HidHide Configuration Client**, uncheck the affected DualSense in **Devices**, or disable **Enable device hiding** temporarily; the latter affects every hidden device. Do not delete the recovery journal before recovery completes. Closing the app cannot make a controller visible if another tool had already hidden it.

Hardware acceptance checks (perform with both USB and Bluetooth):

1. With telemetry stopped, enable emulation and verify the game sees one Xbox, with correct axes/buttons and two-motor vibration.
2. Start telemetry, including a session with no incoming packets, and verify Xbox rumble stops. With valid Forza telemetry, verify native haptics and adaptive triggers still work.
3. Stop telemetry and trigger a new rumble event; verify ordinary vibration resumes without replaying old feedback.
4. Disconnect/reconnect the controller, switch transport, disable emulation, and close the app; verify virtual-device removal/recreation and restoration of physical visibility.
5. Verify missing-driver messages, independent startup preferences, and recovery after an interrupted emulation session. Ordinary automated tests use fake adapters and do not change installed drivers or HidHide settings.

### HIDMaestro setup and FH6 hardware check

The backend selector is saved immediately in `ForzaHaptics.settings.json`, independently of profiles. Existing installations keep ViGEm. Stop Xbox emulation before changing backends; a HIDMaestro failure never silently falls back to ViGEm.

HIDMaestro is pinned to the official **1.9.0 SDK**, with its SHA-256 and MIT notice bundled in the repository. This application uses its own adapter; PadForge does not need to run. HIDMaestro uses the `xbox-series-xs-bt` virtual Xbox Series profile. That profile describes the virtual device and is independent of whether the physical DualSense uses USB or Bluetooth.

1. Install HidHide from its official releases. Close PadForge, DS4Windows, DSX and other controller emulators. Disable **Steam Input** for Forza Horizon 6, then fully close and restart FH6 after changing this setting.
2. Start ForzaHaptics **as administrator**. Disable **Auto-start** and stop telemetry, simulation and tests. Leave Data Out disabled for this experiment.
3. With emulation stopped, select **Xbox Series — HIDMaestro**. Choose **Install / repair HIDMaestro** if setup is required and review the confirmation. The SDK installs/repairs its user-mode virtual controller driver and local trusted certificate. Installation is blocked while another application has active HIDMaestro devices. Normal launch and reconnection never install the driver automatically.
4. Enable **Xbox emulation** before launching FH6. Keep telemetry stopped for the whole test.
5. Drive through several accelerations, wheelspin events and hard braking events, with a quiet interval between them. Note which trigger you feel and roughly when. Test USB and Bluetooth in separate sessions if available.

Disabling HIDMaestro emulation fully removes the virtual Xbox. Windows PnP removal can take **5–11 seconds**, as documented by the [pinned SDK](https://github.com/hifihedgehog/HIDMaestro/blob/v1.9.0/sdk/HIDMaestro.Core/HMContext.cs). The status shows the current phase and elapsed time while the window remains responsive. The toggle and backend selector stay disabled until the transition completes. ForzaHaptics restores access to the physical DualSense before waiting for virtual-device removal. If Windows leaves a device behind, **Check again** verifies removal without creating another Xbox or deleting other applications' devices. Windows readiness does not guarantee that Steam or an already-running game has recognized the new controller. Restarting FH6 after changing Steam Input is a separate requirement, not a remedy for a failed device transition.

For a hardware switching check, perform **10 complete off/on cycles with Steam open and FH6 closed**, then **10 cycles with FH6 running**. Wait for each transition to finish. For every cycle, note transition durations, whether Windows reports the Xbox ready, whether Steam shows Xbox, whether the game accepts input, and whether game feedback arrives. Record those outcomes separately: input or device readiness alone does not confirm rumble. Keep telemetry stopped and verify the physical DualSense returns when emulation is off. These checks require real hardware and are not claimed as completed by automated tests.

Valid incoming HID reports with nonzero LT/RT confirm receipt of Impulse Triggers. No feedback, body-only feedback, and supported reports containing zero trigger values are distinct results; absence of nonzero values in one drive alone does not establish that the game never supports them. XInput feedback does not provide trigger channels. Telemetry and tests retain exclusive priority over game feedback; old effects are discarded when control returns to emulation. Hardware testing is required to confirm the feel of adaptive effects on the physical controller.

## Command line

Console modes remain available. Use `dotnet run --project ForzaHaptics -- --simulate` (application arguments follow `--`) or run `ForzaHaptics.exe --console` from a terminal. Settings come from the active profile unless `--config FILE` is supplied.

```text
ForzaHaptics [options]
  (no options)        open the settings and profiles window
  --console           run without a window: receive FH6 telemetry and drive haptics
  --simulate          use the synthetic drive instead of the game
  --test              test the haptic actuators
  --trigger-test      manual 12-second test of gear cues only
  --record FILE       record telemetry to a file while playing
  --replay FILE       replay a recording in a loop for tuning without the game
  --render FILE.wav   render offline to WAV without a controller (simulation or --replay)
  --list              list audio devices and DualSense HID devices
  --output auto|usb|bt, --port N, --config FILE
```

## Configuration and tuning

All settings are available in the window; editable profiles also have a corresponding file under `Configs`. Built-in Default is available for inspection only. **Gain** controls effect strength; **Enabled** turns an effect on or off.

Tuning tips:

- The status bar displays `surface 0.xx`, the raw `SurfaceRumble` value from the game. Drive on asphalt and dirt, compare the values, then adjust `Road.SurfaceScale` so dirt feels noticeably stronger than asphalt.
- `L 0.xx R 0.xx` shows synthesized signal peaks, not measured actuator force. Adjust overall strength with `Dynamics.Makeup` or `MasterGain`. Peaks near 1.0 are normal: the output stage includes a compressor and soft limiter, so impacts are not hard-clipped.
- Record a drive with `--record lap.fhrec`, then tune against `--replay lap.fhrec` without playing. `--render lap.wav --replay lap.fhrec` produces a WAV whose left and right channels correspond to the two actuators, making the signal easy to inspect in Audacity.
- DualSense uses voice-coil actuators rather than eccentric rotating masses. Frequencies below roughly 60 Hz are barely perceptible, the strongest range is about 100–250 Hz, and frequencies above 350 Hz begin to squeal. Effect carriers therefore sit in the useful range, while low-frequency rhythms such as rumble strips and ABS modulate their amplitude. The output is constrained by the `LowCutHz..HighCutHz` band-pass filter.
- `--test` sweeps from 20 to 400 Hz. Note where vibration feels strongest and, if necessary, move effect `FreqHz` values toward that range.

## Compatibility and troubleshooting

- **DSX / DualSenseY:** Disable *Audio to Haptics* or *Audio passthrough* to prevent mixed signals over USB or conflicts over Bluetooth. Gamepad emulation may remain enabled, but disable competing adaptive-trigger effects.
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
  Emulation/                       Xbox virtual input, HidHide recovery, native rumble and telemetry priority
  Triggers/                        physical input gating, event processing, 100 Hz runtime and effect encoding
  Config/                          profile storage, load/save, hot reload, and live configuration updates
  Gui/App.axaml(.cs)               Avalonia application and Fluent theme bootstrap
  Gui/MainWindow.axaml(.cs)        main Avalonia window and view-specific lifecycle integration
  Gui/Views/SettingsView.axaml     settings editor generated from [Ui] metadata
  Gui/ViewModels/                  MVVM presentation state and commands
  Gui/Services/ and Gui/Dialogs/   platform integrations and asynchronous modal dialogs
  AppConfig.cs                     settings and UI field metadata ([Ui], [UiGroup])
  HapticEngine.cs                  real-time engine shared by GUI and console modes
  Configs/profile_1.json            embedded seed for the first editable profile
  Program.cs                       entry point, console modes, and offline rendering
```

The presentation layer uses **Avalonia 12.1.3** with compiled bindings and **CommunityToolkit.Mvvm 8.4.2**. View models own UI state and commands; code-behind is limited to view-specific lifecycle work. The haptic engine and console modes remain independent of the UI framework.

To add an effect, calculate its level in `TelemetryProcessor.Process`, place it in `HapticTargets` or enqueue a `HapticKick`, then add the corresponding generator in `HapticSynth.Render`.

## Validation

Run `dotnet run --project ForzaHaptics.Tests` for parser, controller lifecycle, view-model, and Avalonia layout tests. Set `FORZAHAPTICS_SCREENSHOT_DIR` to an output folder to render Bluetooth, charging USB, and disconnected states at two window widths and 100%/150%/200% scale.

Hardware diagnostics are opt-in. Set `FORZAHAPTICS_HARDWARE_TEST=1` and `FORZAHAPTICS_HARDWARE_MODE` to `passive`, `haptics`, or `disconnect`, then run `dotnet run --project ForzaHaptics.Tests -- -class ForzaHaptics.Tests.ControllerHardwareTests`. Passive mode only reads status; haptics runs a three-second motor test; disconnect removes the selected Bluetooth connection. Ordinary tests do not access a physical controller.

## Acknowledgements

- Xbox Series emulation and Impulse Triggers feedback: [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) by **hifihedgehog**, SDK and driver under the **MIT** license.
- Bluetooth haptics protocol: **SAxense** by Sdore — <https://apps.sdore.me/SAxense> and <https://github.com/egormanga/SAxense>. The author asks projects to reference the underlying research, so both links are included here. This repository contains an independent C# implementation of the protocol.
- Telemetry format: [Forza Horizon 6 “Data Out” Documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation).
- Ideas and channel verification: [DualSenseY-v2](https://github.com/WujekFoliarz/DualSenseY-v2) and [HorizonHaptics](https://github.com/haritha99ch/HorizonHaptics).
- Libraries: [Avalonia](https://avaloniaui.net/), [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/), [NAudio](https://github.com/naudio/NAudio) (MIT), and [HidSharp](https://www.zer7.com/software/hidsharp) (Apache 2.0).

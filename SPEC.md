# WinSwitch: specification (draft 2, 17 Nov 2026)

Formerly WinPower; renamed because it does more than power (remote control, volume, screen).

## What it is

A tray-only app that puts the PC into the right setup for where it is. You pick a profile from the tray, or WinSwitch picks it for you from what is plugged in (dock, monitor, USB device, charger, battery level). A profile sets Windows power settings, screen, CPU, radios, device power saving and volume. The same controls work remotely over MQTT (Home Assistant, Node-RED) and from an iPhone with no hub. Everything it changes was snapshotted first, and uninstalling puts the PC back exactly as it was.

Same family as WinSqueeze, WinGCal and WinTapeRecorder: C# .NET 8 WPF, Velopack background updates (dev on every push to main, stable promoted by hand with `stable_version`), the same Settings window (left menu under small headings, white cards, tinted callouts, expanders), the same tray menu style (shaded version row at the top), `%LOCALAPPDATA%\WinSwitch-data`, `portable.txt`, crash.log, the No AI attribution check.

**No WebView2 and no Svelte.** There is no main window, and the sibling Settings windows are already plain WPF (WinGCal `SettingsWindow.xaml`). That keeps the install small and the memory use near zero while it sits in the tray. The phone page is one small embedded HTML file.

## README and website opening

The first lines of the README and the download page lead with the remote:

> **WinSwitch: your Windows PC, switched from anywhere in the house.**
> Change power profiles, screen, volume, sleep and shut down from your iPhone, Home Assistant or the tray. And let it switch itself when you dock, unplug or go to bed.

Then three picture tiles: **From your phone** (the phone page), **Home Assistant** (the device card), **By itself** (dock in, Desk profile on).

## Principles

1. **Invisible until needed.** Tray icon only. Everything set up in Settings, everything done daily from the tray menu.
2. **Leave no trace.** Snapshot before the first change, restore any item or everything, restore on uninstall.
3. **Only touch what you asked for.** Every setting in a profile starts as "Leave as it is". A profile changes only what you set.
4. **Remote is off until you turn it on,** and stays on your own network.

## Simple and powerful: dials over details

Every screen shows a few plain-language **dials**; every one of the many settings is still there, one click underneath.

- **A profile is five dials,** each a single choice or slider with plugged-in and battery sides:
  - **Screen:** off after, dim after, brightness.
  - **Sleep and lid:** sleep after, what the lid does.
  - **Battery or speed:** one slider from Longest battery to Fastest. It sets power mode, CPU limits, boost, energy preference, refresh rate, Wi-Fi power saving, USB and PCI Express power saving together, to values tuned for this PC.
  - **Wireless and devices:** Wi-Fi, Bluetooth, which devices may wake the PC.
  - **Sound:** volume and output.
- **Under each dial,** a "What this sets (7)" expander lists the real settings with their values. Change any one and the dial shows **Custom** with a small dot and an "Undo custom" link. Nothing is ever hidden that has been changed.
- **Read as a sentence.** Each profile's card in the list reads like a person wrote it: "Screen off after 30 min, never sleeps, full speed, Wi-Fi off." Situations read the same way: "When the Home dock is connected, use Desk."
- **Find a setting:** a search box at the top of Settings finds any setting by name or by what it does ("lid", "fan", "mouse wakes") and opens the dial with it highlighted.
- **Only what this PC has.** Settings this PC lacks (hibernate, refresh rates, a battery on a desktop) never appear.
- **The tray stays short:** profiles, tweaks, Keep awake, Power. Everything else is in Settings.

## Architecture: tray app plus an optional helper

Two parts sharing one engine library (`WinSwitch.Core`):

- **Tray app** (per user, Velopack, no admin). Tray menu, Settings, detection and profile switching while you are signed in.
- **Background helper** (a Windows service, optional, off by default). Installed from Settings › Helper with one admin prompt.

**Recommendation: offer the helper, don't require it.** Without it WinSwitch still does the main job (power plan settings, brightness, volume, radios, remote while signed in). The helper unlocks what Windows only allows from admin or with nobody signed in:

| Needs the helper | Why |
|---|---|
| Device power saving (Wi-Fi, Bluetooth, USB hubs, mouse/keyboard "allow to wake") | Device Manager power settings are admin-only |
| Wi-Fi adapter power and Wake on LAN properties | Driver properties are admin-only |
| Hibernate on/off, Fast startup | Admin-only |
| Remote control while signed out, at boot, or on a headless server | Only a service runs with nobody signed in |
| Restore of all of the above on uninstall | Same reason |

When the helper is installed, it owns remote control and profile switching (so a server or a signed-out laptop still answers), and the tray app becomes its window onto it over a local named pipe that only the signed-in user and admins can open.

**Helper security.** It runs as LocalSystem, so its files live in `C:\Program Files\WinSwitch Helper` (admin-only), never in the user-writable Velopack folder, otherwise any program could swap the exe and get SYSTEM. It accepts only a fixed list of commands (switch profile, set a known setting, power actions), never paths or command lines. The helper is kept small and changes rarely; when an update carries a new helper version, the tray shows "Update the helper" with one admin prompt.

## Tray

Left-click opens the profile menu, right-click the full menu:

- Shaded version row (as the siblings), plus **Restart to update to x.y.z** in bold when an update is downloaded (there is no window to put the update bar in, so it lives here and in Settings › About, and the icon gets a small dot).
- **Automatic** (ticked when the situation rules are choosing) with what it matched underneath in grey ("Desk dock, plugged in").
- The profiles, one per row with their colour dot; the active one ticked. Picking one by hand holds until the next situation change (plug or unplug something), then Automatic takes over again.
- **Keep awake** ▸ 30 min, 1 hour, 2 hours, Until I turn it off (stops sleep and screen off; the icon shows a small cup).
- **Screen off now**, **Lock**, **Sleep**, **Hibernate**, **Restart**, **Shut down** (power actions in a submenu "Power").
- **Volume** ▸ slider row and Mute (handy on servers with no keyboard volume keys).
- Settings, Quit.

The icon is the app icon with the active profile's colour as a small dot. Hover text: "WinSwitch: Desk (plugged in, 82%)".

## Profiles

A profile is a named set of settings. Each power setting has **two values, Plugged in and On battery**, exactly as Windows stores them, so one profile covers both and unplugging the charger needs no rule. Settings that have no battery side (volume, radios, device power) have one value.

Besides Starting profile, the wizard makes the profiles you need (below). Without the wizard these are offered as templates: **Out and about** (battery-friendly), **Desk** (screens stay on longer, full CPU), **Presenting** (nothing sleeps or dims), **Server** (never sleeps, screen off quickly, Wake on LAN on). Duplicate a profile to start a new one.

Settings › Profiles is a list of profile cards on the left and the chosen profile's five dials on the right (see Simple and powerful). Inside a dial's expander, settings sit in two columns (plug icon, battery icon), each showing its current Windows value in grey and "Leave as it is" until set. The full list of what the dials reach:

### What a profile can set

**Screen**
- Turn off screen after (plugged / battery)
- Dim screen after, and dimmed brightness
- Brightness of the built-in screen (external monitors untouched)
- Adaptive brightness and content adaptive brightness
- Turn off screen when locked after (the lock-screen timeout Windows hides)
- Refresh rate of the built-in screen (e.g. 60 Hz on battery, 120 Hz plugged; a big battery saver)

**Sleep**
- Sleep after, hibernate after (plugged / battery)
- Hybrid sleep, wake timers, require sign-in on wake
- Network while asleep (Modern Standby PCs: stay connected or disconnect; disconnecting saves a lot of battery in a bag)
- Hibernate after this long asleep on battery (Modern Standby's standby budget)
- Lid close, power button and sleep button actions (do nothing, sleep, hibernate, shut down, screen off)

**Battery** (your "sub-profiles": the low-battery behaviour belongs to the profile)
- Low battery level, notification and action
- Critical battery level and action (sleep, hibernate, shut down)
- Reserve battery level
- Energy saver turns on at %, and its screen brightness

**CPU and performance**
- Windows power mode (Best power efficiency, Balanced, Best performance)
- Minimum and maximum processor state, maximum frequency
- Processor boost mode
- Energy performance preference
- Cooling policy (fan first or slow down first)

**Wireless and devices**
- Wi-Fi adapter power saving (Windows' own setting, no admin)
- Wi-Fi on/off, Bluetooth on/off (e.g. Wi-Fi off at the desk on wired Ethernet)
- USB selective suspend, PCI Express link power saving, hard disk off after
- Allow Windows to turn off this device to save power: per device (Wi-Fi, Bluetooth, USB hubs, Ethernet). Helper.
- Allow this device to wake the PC: per device (stops a mouse in the bag waking the laptop). Helper.
- Wake on LAN on the Ethernet adapter. Helper.

**Sound**
- Volume and mute
- Default output device (dock speakers at the desk, laptop speakers away)

**Hibernate and startup** (helper): Hibernate available, Fast startup.

Power settings are written to WinSwitch's own power plan, made on first run as a copy of your current plan and made active. Your original plan is never edited, so restoring is just making it active again. Settings outside the plan (brightness, devices, radios, volume, refresh rate) are written directly and covered by the snapshot.

## Apps to stop (next version)

Each profile can list **Apps to stop**: background apps that drain the battery (Syncthing, cloud sync, updaters, RGB tools). A sixth card on the profile page, "Apps to stop", with Add (pick from running apps) and each app's row showing how it was started.

- **Switching to the profile:** WinSwitch asks each app to close the way Windows does at sign-out (a close message to its windows, Ctrl+C for console apps like Syncthing), waits up to 10 seconds, then ends it. Apps running as a Windows service are stopped as a service (helper).
- **Switching away:** WinSwitch starts again exactly what it stopped (same program, same arguments, same folder, as you), unless the new profile stops it too. Something you closed yourself, or that wasn't running, is never started.
- **Safety:** never system processes, never WinSwitch, never an app with a visible window that has unsaved work unless you tick "Even if it has windows open". A notification says what it stopped: "Stopped Syncthing to save battery. [Start it now]".
- Remembered across restarts, so a crash or reboot on battery still restarts the apps when you plug in.

## Tweaks (day and night, and other small changes)

A tweak is a few settings laid over whichever profile is active, so you don't need a "Desk night" copy of every profile. Example **Night**: turn off screen after 2 minutes, volume down 20% (or set to 25%). While a tweak is on, the tray shows "Desk + Night" and only the tweak's settings differ.

A tweak turns on and off by any of:
- **Schedule:** fixed times (Night 22:00 to 07:00), or sunset to sunrise (from Windows' location if allowed, otherwise your time zone).
- **Remote:** an MQTT message or the phone page (Home Assistant decides when it's night, bedtime, away).
- **Tray:** tick it by hand (holds until its next scheduled change).

Volume in a tweak is "Leave", "Set to X%" or "Up/down by X%", and goes back to what it was when the tweak ends (unless you changed it yourself in the meantime).

## Notifications

WinSwitch's own small glass cards, sliding up just above the tray clock (as WinGCal's next-event pop-out), never Windows notifications: Windows' own can be silenced by Focus, can't hold a proper question, and pile up in the Action Centre. Two kinds:

**Info** (fades after 5 seconds, stays while the pointer is on it, one line plus an Undo):
- "Desk profile on. Home dock connected." [Undo]
- "Out and about. On battery, about 4 h 10 min left."
- "Night tweak on until 07:00." / "Home Assistant turned on Night." (remote changes always say where they came from)
- "Update ready." [Restart]

**Questions** (stay until answered or ×; one at a time, the rest wait):
- New dock or monitor seen: "This is a new place. What should it use?" [Set up new profile] [Use Desk] [Ignore this]. "Set up" opens a two-question mini wizard (screen, sleep) with Desk's values pre-filled.
- Changed in Windows: [Save to Desk] [All profiles] [Just for now].
- Suggestions (at most one a week).

An unanswered question (× or ignored) waits in the tray menu ("1 question" at the top, with a dot on the icon) and in Settings, and is never lost.

Quiet by design: info cards are skipped while a full-screen app or Presenting is on, and while Windows' Do not disturb is on; questions wait until afterwards. Several changes within a few seconds (a dock connecting in stages) make one card. Settings › General › Notifications: **Profile changes** Show (default) / Questions only / Off, and **Sound** off by default.

## Playing nicely with Windows

**WinSwitch works through Windows' own settings, never around them.** Power settings go into a real Windows power plan, so Windows Settings, the Control Panel and `powercfg` all show the true values, and anything Windows does on its own (battery saver, lid close) behaves as normal.

**Tools: the documented Windows APIs, never shelling out to `powercfg` or editing the registry directly.**

| Area | API | Stability |
|---|---|---|
| Power plan settings, plans | `powrprof` (PowerReadACValueIndex / PowerWriteACValueIndex, PowerSetActiveScheme), the same calls Windows Settings uses | Documented, unchanged since Vista |
| Change notifications | PowerSettingRegisterNotification, RegisterDeviceNotification, display change messages | Documented |
| Brightness | WMI WmiMonitorBrightnessMethods | Documented |
| Refresh rate | ChangeDisplaySettingsEx | Documented |
| Wi-Fi/Bluetooth on/off | Windows.Devices.Radios (WinRT) | Documented |
| Volume | Core Audio via NAudio (as WinTapeRecorder) | Documented |
| Device power and wake (helper) | WMI MSPower_DeviceEnable / MSPower_DeviceWakeEnable, what Device Manager uses | Documented |
| Power mode (efficiency/balanced/performance) | PowerSetActiveOverlayScheme | **Undocumented** (stable since 2017) |
| Default output device | IPolicyConfig | **Undocumented** |

The two undocumented ones are marked fragile: after every write WinSwitch reads the value back, and if Windows didn't take it (or a Windows update broke it) that setting shows "Not working on this version of Windows" and is left alone, rather than fighting Windows. Every other setting gets the same read-back, so a setting a PC doesn't have (no hibernate, no refresh rate choice) is simply hidden.

**Changes you make in Windows win.** WinSwitch watches its settings for changes it didn't make (Windows Settings, Control Panel, Device Manager, another app). The change is kept straight away, never overwritten, and a WinSwitch question appears (see Notifications):

> You changed **Lid close (plugged in)** to **Hibernate** in Windows.
> [Save to Desk] [Save to all profiles] [Just for now]

"Just for now" (or ignoring the tip) keeps it until the next profile switch. Unanswered changes also wait in Settings › Profiles under a "Changed in Windows" callout with the same three buttons. If someone who didn't know WinSwitch was there changes something, it keeps working the way they set it.

## Situations (automatic switching)

A situation is "when this is connected, use that profile". Settings › Situations is an ordered list; the first one that matches wins, and the last one is always **Anything else** (e.g. Out and about).

A situation can match any of:
- A **dock** (learned, see below)
- A **monitor** (by its own id, so the same monitor model on another desk is different)
- A **USB or Bluetooth device** (a particular keyboard, a DAC)
- A **network**, recognised by its router (default gateway's hardware address), not the Wi-Fi name: Windows 11 24H2 and later only reveals Wi-Fi names to apps with Location turned on
- **Battery under** a level (e.g. under 20% use Saver, whatever is plugged in)
- **Nobody signed in** (helper only; for servers)

Power source is not a situation trigger, because every profile already has its Plugged in and On battery values. "Charger only" is just the Anything else profile's Plugged in side. If you ever want charger-only to be a different profile from the dock, the dock situation above it catches the dock first.

**Learning a dock** (task-based, no device ids to type): Settings › Situations › Add › "Learn what I plug in". It asks you to unplug, then plug in, and lists what appeared ("Dell WD19 USB hub, Realtek USB Ethernet, DELL U2720Q, Logitech receiver"). You name it ("Desk dock") and it matches when most of them are present, so a dock still matches with a monitor switched off. Monitor ids reuse WinGCal's monitor-setup code.

Changes are debounced (2 seconds after the last plug event) so a dock connecting in stages switches once. Each switch shows an info card (see Notifications); a dock or monitor it has never seen asks what to do.

## Tested on a real laptop (read only)

A Dell XPS 17 on Windows 11 26H2, on a USB-C dock. What it taught the design:
- **Docks have no identity of their own.** This one shows up only as a generic Microchip USB hub, a Realtek USB Ethernet adapter and whatever is plugged into it (Acer BM320 monitor, a USB keyboard, a Wacom). So "Learn what I plug in" combines the monitor's EDID serial, the Ethernet adapter's MAC and the hubs, and matches on most of them. The Ethernet adapter counts as present even with its cable out.
- **Modern Standby only** (no classic S3 sleep), so "Sleep" means connected standby. Hence the two new Sleep rows above (network while asleep, hibernate after time asleep).
- **A custom plan was already active** (a custom one, alongside Balanced and Dell). WinSwitch's own plan copies whichever is active, and Starting profile carries its values.
- **Power mode differs plugged in vs battery** (Balanced / Best power efficiency), so power mode is a two-sided row like the rest.
- **Device power and adapter properties are readable without admin** (Wake on LAN, power saving flags); writing them needs the helper. `Get-NetAdapterPowerManagement` fails on this PC, so WinSwitch reads them through WMI (MSPower_*) and the adapter's advanced properties instead.
- **Wake devices:** the dock's Ethernet adapter, the fingerprint reader and the NVIDIA USB-C controller can wake it. A natural suggestion for Out and about.
- **Vendor tools:** a Dell plan exists. If Dell Optimizer or Dell Power Manager is running, Settings says so, since they can change the same settings behind WinSwitch's back (handled by Changed in Windows).

## First run

**1. Starting profile.** Before anything else, WinSwitch reads everything it can about how this PC is set up now and saves it twice: as Original settings (the untouchable restore record, below) and as an editable profile called **Starting profile**, with every setting filled in (Windows power plan both sides, power mode, brightness, refresh rate, radios, volume and output device, and with the helper, every device's power saving and wake flags, Wi-Fi and Ethernet adapter power and Wake on LAN properties, hibernate, fast startup). So switching to Starting profile always means "how it was", and it is the base the wizard's suggestions start from. Installing the helper later fills in the device rows it couldn't read before.

**2. Setup wizard.** A few questions, one per page, big picture buttons, nothing technical. Each answer is pre-picked from what WinSwitch can see (a battery, a dock plugged in now, external monitors):
1. What is this? Laptop, Desktop, or Always-on server.
2. Where do you use it? At a desk on a dock, Out and about, or Both. If a dock is plugged in now: "Is this your home dock?" and it learns it on the spot (no unplugging needed).
3. How long should the screen stay on while you're away from it? Day (e.g. 30 min) and night (e.g. 2 min).
4. When is night? Fixed times, Sunset to sunrise, or Home Assistant tells me.
5. Volume at night: Leave it, Lower it, or Set it.
6. On battery, what matters more? Longest battery, Balanced, or Fastest.
7. When you close the lid away from the desk: Sleep, then hibernate after an hour (recommended), Hibernate, or Shut down.
8. Control it from Home Assistant or your phone? Yes / Later.

The last page shows what it will make, as plain sentences you can change before pressing **Create**: "Desk (on the Home dock): screen off after 30 min, never sleeps, full CPU", "Out and about: …", "Night tweak 22:00 to 07:00: screen off after 2 min, volume down 20%". Run it again any time from Settings › Profiles › Set up with the wizard (it offers to replace or add).

**3. Suggestions.** Afterwards WinSwitch suggests improvements from what it notices, at most one a week, as a tip and a callout in Settings › Profiles with Accept and No thanks (never asked twice):
- "You plug into the same dock every evening. Make it a situation?"
- "The battery dropped 18% asleep in your bag last night. Hibernate after 1 hour asleep on battery?"
- "The screen is always set to full brightness by hand on battery. Save that to Out and about?"
- "The Wi-Fi stays connected while wired at the desk. Turn Wi-Fi off in Desk?"

## Original settings (snapshot and restore)

**On first run, before changing anything** (the same read as Starting profile), WinSwitch records every value it can ever touch: the active power plan and every setting in the list above (both sides), power mode, brightness, refresh rate, radios, volume and output device, and, once the helper is installed, every device's power and wake flags, adapter properties, hibernate and fast startup. Saved as `original-settings.json` in the data folder, plus a copy the helper keeps in `C:\ProgramData\WinSwitch` so it survives a deleted user folder.

Settings › Original settings:
- One row per setting: Original value, Current value, and **Restore** when they differ.
- **Restore everything** puts the PC back as it was before WinSwitch and stops automatic switching (a callout says so and offers to turn it back on).
- Devices added later (a new mouse) are snapshotted the first time WinSwitch sees them, before any change.

**Uninstall** (Velopack uninstall hook) restores everything, then removes WinSwitch's power plan, and asks the helper to restore its part, remove itself and delete its folder. If the helper can't be reached, the uninstaller says so and gives a "Restore and remove helper" button that asks for admin.

## Remote control

Off by default. Settings › Remote has two cards. Both offer the same actions, each of which can be turned off individually (e.g. allow profile and volume but not Shut down).

**Actions:** switch profile (or Automatic), turn a tweak on or off (Night), shut down, restart, sleep, hibernate, lock, screen off, keep awake for N minutes, volume and mute, brightness.
**State:** active profile, automatic or held, situation, power source, battery % and charging, screen on/off, idle time, volume, signed-in or not, WinSwitch version.

### MQTT (Home Assistant, Node-RED)

- Broker address, port, username, password, TLS on/off (MQTTnet library).
- **Home Assistant discovery on by default** once MQTT is on: the PC appears as a device with a Profile select, a switch per tweak, Power buttons, Volume and Brightness numbers, Mute and Keep awake switches, and battery/power/screen sensors. Online/offline via a last-will message.
- Plain topics for Node-RED: `winswitch/<pc name>/profile/set`, `.../volume/set`, `.../power/set` (`shutdown`, `sleep`…), state on `winswitch/<pc name>/state` as one JSON message.
- Turning the PC **on** remotely is Wake on LAN, which WinSwitch can't send to itself. WinSwitch turns WoL on (helper) and publishes the MAC address, so Home Assistant's Wake on LAN button works.

### iPhone, no hub

**Recommendation: a built-in phone page on your home network, which Shortcuts can also call.**
- Settings › Remote › Phone shows a QR code. Scan it with the iPhone camera: Safari opens the PC's page already paired; Add to Home Screen makes it an app icon with profile buttons, volume, and power actions (shut down asks "Are you sure?").
- The same addresses work in Shortcuts ("Get contents of URL", POST, the token in a header), so Siri can say "Hey Siri, laptop to Desk". Settings shows a copyable example for each action.

**Security:**
- Only answers devices on the local network (private addresses); refused from anywhere else, even if a router forwards the port.
- A long random token, in the QR code and never shown in full; **New token** unpairs every phone. Wrong tokens are slowed down and logged.
- Plain HTTP on the home network (iPhones refuse self-signed HTTPS without a profile install, which is worse). From outside the house use a VPN such as Tailscale; WinSwitch never opens itself to the internet.
- Windows Firewall rule added for the private network profile only, removed on uninstall.
- Logs every remote command to `logs\remote.log` (who, what, when).

## Settings window

Larger than the siblings' by default (about 1100 × 760), since it is the whole app. Same layout:

- **Power:** Profiles; Situations; Original settings.
- **Remote:** MQTT; Phone and Shortcuts; Allowed actions; Remote log.
- **App:** General (start with Windows on by default, tray left-click behaviour, switch tips); Helper (install, status, update, remove); Appearance (colour, scale, as the siblings); About (version, update channel, versions list, privacy, about the developer).

First run opens Settings on the wizard (above), with a line at the top: "I saved your current settings as Starting profile, so you can always go back."

Naming: WinSwitch always says **profile**. Windows' word "power plan" is only used for the plan WinSwitch keeps in Windows underneath.

## Privacy

Nothing leaves the PC except the update check to GitHub and the MQTT broker you configure. The phone page is local only. About says so, as WinSqueeze's privacy callout.

## Later (not in the first build)

- Battery charge limit (e.g. stop at 80%) for Lenovo, Dell, ASUS, Surface, which each have their own vendor interface.
- Profile on a schedule (e.g. Server profile overnight).
- Run a program or script when a profile starts (behind an allow-list).

## Build phases

1. Shell: tray, Settings window, updater, start with Windows, snapshot, Starting profile and Original settings with restore, own power plan. Profiles with Screen, Sleep, Battery, CPU. Changed-in-Windows tips.
2. Situations and tweaks: docks, monitors, USB, network, battery level, learn a dock, schedule. Setup wizard.
3. Helper: service install, device power and wake, adapter, hibernate, uninstall restore.
4. Remote: MQTT with Home Assistant discovery, phone page, Shortcuts, allowed actions, log.
5. Sound, radios, refresh rate, Keep awake polish, suggestions.
6. Apps to stop per profile.

Repo: Mat creates `hazymat/winswitch` (or the chosen name) on GitHub; this file becomes its SPEC.md.

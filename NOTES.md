# Plug & Pray: build notes and landmines

Engineering log for rebuilding a replacement for the ImpulseRC Driver Fixer.
Everything here was verified on real hardware, not assumed. Kept in the repo because
most of it is knowledge you cannot get from libwdi's own docs, and losing it is how the
original tool ended up unmaintainable.

Read this before trying to build libwdi by hand. `scripts/Build-LibWdi.ps1` already
automates every fix described below.

## Why this project exists

ImpulseRC (the company) folded around January 2026. The Driver Fixer itself is
**not gone** — https://github.com/ImpulseRC/ImpulseRC_Driver_Fixer still serves the
binary. But it is:

- binary-only (no source published, ever)
- no licence file
- .NET Framework 4.5
- unmaintained, and one lapsed GitHub account from disappearing
- STM32-only (never handled AT32 / GD32, which newer FCs use)

So this is a *maintainable replacement*, not a rescue mission. Pitch it that way.

## Licence map (IMPORTANT)

libwdi's repo mixes two licences. Getting this wrong contaminates the whole project.

| Path | Licence | Rule |
|---|---|---|
| `libwdi/*` (the library) | **LGPL v3** | Dynamically link `libwdi.dll` → our app keeps its own licence |
| `examples/wdi-simple.c` | **LGPL v3** | Safe to read + reference for install flow |
| `examples/zadig.c` (GUI) | **GPL v3** | **DO NOT COPY FROM.** One line → whole app becomes GPLv3 |

Decision: link `libwdi.dll` dynamically. Read `wdi-simple.c` only. Treat `zadig.c` as
look-don't-touch. Our own app licence is still an open choice (MIT / GPLv3 / other).

Do **not** decompile ImpulseRC's .exe — proprietary, no licence, and unnecessary.

## Landmine 1: libwdi has no prebuilt binaries, by design

Pete Batard's stated position: libwdi compiles ~18 different ways (static/dynamic,
which driver embedded, 32/64/ARM64), so publishing binaries would be unsupportable.
Not in vcpkg. Not in NuGet. **Compiling it ourselves is the only door.**

## Landmine 2: WDK_DIR is mandatory for WinUSB

`libwdi/libwdi.c` ~line 724:

```c
case WDI_WINUSB:
#if defined(WDK_DIR)
	return TRUE;
#else
	return FALSE;
#endif
```

Without `WDK_DIR` defined, libwdi reports **WinUSB as unsupported** — which is the one
driver we actually need. And `libwdi/embedder.h:26` hard-`#error`s unless at least one
of `WDK_DIR` / `LIBUSB0_DIR` / `LIBUSBK_DIR` / `USER_DIR` is set.

`msvc/config.h` defaults `WDK_DIR` to `C:/Program Files (x86)/Windows Kits/8.0`, which
does not exist on a modern machine. This is the cause of the "can't build libwdi"
reports (e.g. libwdi issue #238).

### Required files

`embedder_files.h` wants, under `WDK_DIR`:

```
redist\wdf\x64\WdfCoInstaller01011.dll     (WDF_VER 1011)
redist\wdf\x64\winusbcoinstaller2.dll      (COINSTALLER_DIR "wdf", X64_DIR "x64")
```

Modern WDK 10 **does not ship these**. They are WDK 8.0-era redistributables.

### Solution (verified 2026-07-17)

The fwlink in `config.h` still resolves and the file is still live:

```
https://go.microsoft.com/fwlink/p/?LinkID=253170
  -> 301 -> https://download.microsoft.com/download/0/5/F/05FD6919-6250-425B-86ED-9B095E54065A/wdfcoinstaller.msi
     200 OK, 29.87 MB, Last-Modified: Fri, 17 Jan 2025
```

Plan: download that MSI, extract (msiexec /a for admin install, no system install
needed), point `WDK_DIR` at the extracted tree, adjust `WDF_VER` / `COINSTALLER_DIR` /
`X64_DIR` in `msvc/config.h` to match the actual layout. **Verify the layout after
extraction — the constants above are config.h's defaults, not confirmed against this MSI.**

Note: coinstallers are a legacy (pre-Win8) mechanism and are deprecated on Win10+.
`winusb.inf.in` already omits them for ARM64. A future cleanup could patch libwdi to
drop them for a Win10/11-only build — that patch would be publishable under LGPL.
Not worth it for MVP.

## Landmine 3: the trusted-certificate step

libwdi does NOT ship a driver. `winusb.sys` is already inbox and Microsoft-signed.
libwdi generates an `.inf` that *binds* it (see `winusb.inf.in`), generates a `.cat`,
**self-signs the catalog, and installs a certificate into the trusted root /
trusted publisher stores** to satisfy driver signature enforcement.

- `wdi_install_trusted_certificate()` does this
- requires elevation ("must be run from an application running with elevated
  privileges on platforms with UAC else it will fail")

This is exactly what Zadig does. It is normal — but "our tool installs a root
certificate" WILL get scrutinised by the FPV community. **Document it prominently and
honestly in the README rather than letting someone discover it.**

## The technical problem being solved

Windows binds drivers per VID/PID. An STM32 FC presents two unrelated identities:

| FC state | Windows sees | VID:PID | Driver needed | Purpose |
|---|---|---|---|---|
| Normal | Virtual COM Port | `0483:5740` | `usbser.sys` (inbox) | Betaflight settings |
| DFU / bootloader | STM32 BOOTLOADER | `0483:DF11` | **WinUSB** | Flashing firmware |

Windows treats these as different devices and routinely mis-binds the DFU one →
"STM32 BOOTLOADER" with a yellow bang → Betaflight can't flash.

WinUSB specifically, because Betaflight Configurator reaches DFU via libusb, and
libusb on Windows can only talk to devices bound to WinUSB (or libusbK). "Correct
driver" here means "the one that gets out of the way".

Still true in 2026 — confirmed against Betaflight docs and Oscar Liang's guide.

## VID/PID table (to live in a data file, not hardcoded)

Scope decision: **STM32 first, designed for more.** Put this in JSON/TOML so adding
AT32/GD32 later is config, not a rewrite.

| Chip / bridge | VID:PID | Mode | STATUS |
|---|---|---|---|
| STM32 DFU bootloader | `0483:DF11` | DFU | verified |
| STM32 VCP | `0483:5740` | VCP | verified |
| Silicon Labs CP210x | `10C4:EA60` | UART bridge | from memory — VERIFY |
| CH340 | `1A86:7523` | UART bridge | from memory — VERIFY |
| FTDI FT232 | `0403:6001` | UART bridge | from memory — VERIFY |
| Artery AT32 | `2E3C:DF11`? | DFU | UNVERIFIED — research before adding |
| GigaDevice GD32 | ? | DFU | UNVERIFIED — research before adding |

Only the two STM32 entries are confirmed. Verify the rest against real hardware /
official docs before shipping them.

## The "kick" into DFU mode

If the FC is present as a COM port, reboot it into bootloader programmatically:

- Betaflight CLI: open serial, send `bl`
- or MSP: `MSP_SET_REBOOT` (code 68) with the bootloader flag

Both need verifying against a real FC. The "magic baud rate" (1200-baud touch) is an
Arduino/AVR convention — **do not assume it applies to STM32/Betaflight** without
testing.

## Toolchain required

| Need | Why |
|---|---|
| .NET SDK 8 | app; machine had runtimes only, no SDK |
| VS 2022 Build Tools + VCTools workload | libwdi is C, needs MSVC |
| Windows SDK | comes with VCTools `--includeRecommended` |
| WDF coinstaller MSI (above) | satisfies `WDK_DIR` |

```powershell
winget install --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

## Build order

1. Install toolchain (above)
2. Download + extract `wdfcoinstaller.msi`, inspect actual layout
3. Patch `vendor/libwdi/msvc/config.h`: `WDK_DIR`, `WDF_VER`, `COINSTALLER_DIR`, `X64_DIR`
4. Build `libwdi_dll.vcxproj` x64 Release → `libwdi.dll`
5. Smoke test with `wdi-simple.exe` against a real FC in DFU mode **before** writing any UI
6. C# app: P/Invoke `libwdi.dll` (enumerate → prepare → install), WinForms, elevation manifest
7. Serial kick to DFU
8. Verify end-to-end on real hardware

Step 5 is the real go/no-go. If `wdi-simple` can't bind WinUSB to `0483:DF11`, nothing
downstream matters.

## BUILD LOG — libwdi compiled successfully (2026-07-17)

Five distinct blockers cleared, all documented above. In build order they hit as:

1. **ARM64 has no cross-compiler** — VS BuildTools here is Hostx64 → x64/x86 only.
   Fix: `OPT_ARM` commented out in `msvc/config.h`; `installer_arm64` ProjectReference
   removed from BOTH `libwdi_dll.vcxproj` and `libwdi_static.vcxproj`; arm64 `Build.0`
   lines removed from `libwdi.sln`.
2. **DLL project not checked for build** — `libwdi (dll)` had `ActiveCfg` but no
   `Build.0` lines in the .sln (upstream ships DLL opt-in, static by default).
   Fix: added `Build.0` lines for all four configs.
3. **Prebuild `embedder` step exited 9009** — command is `cd $(ProjectDir)\.. ` then
   bare `embedder embedded.h`. Two problems: `cd` without `/d` won't switch drives (our
   shell cwd was on G:), and `NoDefaultCurrentDirectoryInExePath=1` is set in this
   environment so cmd won't resolve a bare exe name from cwd. Fix: `cd /d` + `.\embedder`
   in both dll and static vcxproj (all 8 occurrences).
4. **Embedder tried to bundle libusb0/libusbK** from placeholder `D:\` paths that
   don't exist. Fix: commented out `LIBUSB0_DIR` and `LIBUSBK_DIR` in `config.h`
   (we only ship WinUSB).
5. **WDK_DIR** (the known one) — repointed to the extracted MSI redist tree.

### Artifacts (x64 Release)
- `vendor/libwdi/x64/Release/dll/libwdi.dll` — 3.8 MB, x64, exports full `wdi_*` API
  as `__stdcall` (clean P/Invoke target)
- `vendor/libwdi/x64/Release/examples/wdi-simple.exe` — 3.3 MB, statically linked

### Extract-only smoke test (no admin, no hardware) — PASS
`wdi-simple -v 0x0483 -p 0xdf11 -t 0 -n "STM32 BOOTLOADER" -x` produced a correct
WinUSB `usb_device.inf` targeting `VID_0483&PID_DF11`, binding `WinUSB.sys`, with the
`WdfCoInstaller01011.dll` + `WinUSBCoInstaller2.dll` coinstallers extracted. Confirmed
`No .cat file generated (missing elevated privileges)` — the cert/signing step needs
elevation, exactly as predicted (Landmine 3).

### All patched files (for reproducing / upstreaming later)
- `vendor/libwdi/msvc/config.h` — WDK_DIR, OPT_ARM off, LIBUSB0/LIBUSBK off
- `vendor/libwdi/libwdi.sln` — DLL Build.0 added, arm64 Build.0 removed
- `vendor/libwdi/libwdi/.msvc/libwdi_dll.vcxproj` — arm64 ref removed, prebuild cd /d + .\
- `vendor/libwdi/libwdi/.msvc/libwdi_static.vcxproj` — same

### HARDWARE GO/NO-GO — PASSED (2026-07-18)

Tested against a real flight controller: native-USB STM32, normal mode = `USB Serial Device (COM6)`
`VID_0483&PID_5740`, healthy (`usbser`, no problem flag).

**Serial kick works.** `Send-BootloaderKick.ps1 -PortName COM6` sent MSP_SET_REBOOT
(`24 4D 3C 01 44 01 44` = cmd 68, payload 1 = MSP_REBOOT_BOOTLOADER_ROM). Board
re-enumerated `COM6 → VID_0483&PID_DF11` (DFU) within 2s. No button, no Betaflight.

**Key discovery:** the board was ALREADY WinUSB-bound from a prior ImpulseRC run —
`DriverDesc="ImpulseRC Flight Controller"`, and the leftover cert in TrustedPublisher/Root
reads `CN=... (libwdi autogenerated)`. This PROVES ImpulseRC's tool was itself a libwdi
wrapper — our architecture reproduces it exactly. Also means normal mode "just working"
is because Windows persists driver bindings per VID/PID.

**Our binary's install committed successfully** (elevated, EXITCODE=0). After install:
- `DriverDesc`: ImpulseRC Flight Controller → **FC Driver Fixer WinUSB** (our -n)
- `DriverProvider`: libusb.info → **libwdi**
- `Service`: WinUSB (target state held)
- New `oem233.inf` (provider libwdi) registered; Status OK / CM_PROB_NONE

Gotcha found: elevated `wdi-simple` MUST get an absolute `-d <dir>`; default relative
`usb_driver\` fails from the System32 cwd an elevated process inherits (error -11,
"Could not create file"). The C# app must always pass an absolute extraction dir.

The `0x800b0109` "root certificate not trusted" syslog lines during install are NORMAL:
libwdi self-signs, the root check fails, then `0xe0000241` confirms "signed by trusted
publisher" (its cert is in TrustedPublisher) and the force-install proceeds. Not an error.

**State left behind (for the undo/clean feature):** two OEM infs now exist for this
device (oem232 ImpulseRC + oem233 ours). A well-behaved tool should manage/clean these.
Board is in DFU mode — replugging USB returns it to normal firmware (COM) mode.

## MVP APP BUILT + VALIDATED (2026-07-18)

Solution `app/FcDriverFixer.sln`, three projects (.NET 8, x64):
- **FcDriverFixer.Core** — the logic, no UI, testable:
  - `LibWdi.cs` — P/Invoke over libwdi.dll (struct layout verified correct on hardware:
    a bad `wdi_device_info` layout would AV or return garbage VIDs; it returns clean data)
  - `FcCatalog.cs` — data-driven VID/PID table (STM32 verified; bridges/AT32 listed
    unverified). Adding chips = table edit.
  - `Diagnosis.cs` — diagnose-first engine + registry COM-port lookup (no WMI dep)
  - `BootloaderKick.cs` — MSP_SET_REBOOT over System.IO.Ports (mirrors the proven PS1)
  - `Fixer.cs` — orchestrates safely: ONLY installs WinUSB on a DFU device, never
    touches a healthy VCP. Verifies against a re-scan, not just the return code.
- **FcDriverFixer.Cli** (`fcfix.exe`) — headless harness + real deliverable:
  `fcfix list|diagnose|kick <COM>|fix`
- **FcDriverFixer.App** — WinForms one-button GUI. REMOVED 2026-07-18, superseded by
  the WPF app. (Historic: needed `requireAdministrator` in the manifest, and DPI set via
  the `ApplicationHighDpiMode` csproj property rather than the manifest.)

### Full flow validated through our own C# code (not wdi-simple), elevated:
```
NormalMode/COM6 -> [C# MSP kick] -> DFU -> [C# P/Invoke WinUSB install] -> verify -> DfuReady
EXITCODE=0; DriverDesc now "FC Driver Fixer (WinUSB)", Provider libwdi, Service WinUSB
```
So every link is proven in managed code: detection, registry COM lookup, serial kick,
libwdi install, post-verify. Install extract dir = `%TEMP%\FcDriverFixer\driver` (absolute,
per the elevated-cwd gotcha). libwdi cleanly deleted+recreated its cert on reinstall.

### Build/env gotchas hit
- No NuGet source was configured on this machine ("No sources found"); added nuget.org.
  Needed for `System.IO.Ports` 8.0.0.
- WinForms DPI must be set via `<ApplicationHighDpiMode>` csproj property, not the
  manifest (warning WFAC010) — moved it.

### NOT yet done
- GUI not yet visually tested / screenshotted.
- Undo/revert feature (remove WinUSB, restore stock) — designed-for, not built.
- Charge-only cable messaging exists (NothingDetected) but not a positive "cable is
  data-capable" signal.
- Packaging: self-contained single-file publish + code signing (SmartScreen).
- AT32/GD32 entries still commented out (unverified).

## UNDO / REVERT FEATURE BUILT + VALIDATED (2026-07-18)

New `Core/DriverStore.cs`: reads the driver binding via registry
(`Enum\USB\VID&PID\<inst>` -> `Driver`="{classGuid}\NNNN" -> `Control\Class\...` ->
`InfPath`="oemNN.inf", `ProviderName`), and reverts via `pnputil /delete-driver
<oem> /uninstall /force` + `/scan-devices`. Only OEM (`oemNN.inf`) packages are ever
deleted — never inbox drivers.

New actions in the explicit `FixAction` model: `Undo` (remove WinUSB we installed on
DFU) and `RepairVcp` (restore stock serial driver on a Zadig-damaged COM port).
New `FcState.VcpDriverWrong` detects a VCP bound to something other than `usbser`.
CLI gained `fcfix undo` and `fcfix repair`. GUI gained a secondary link (shows
"Undo (remove installed driver)" in the DfuReady state).

### KEY BUG found + fixed on real hardware — multi-package fallback
First undo reported success but device STAYED WinUSB-bound. Cause: the test machine had THREE
matching WinUSB packages (oem232 ImpulseRC + oem233 wdi-simple + oem234 our fix);
removing only the currently-bound one made Windows fall back to the next. Fix:
`RestoreStock` now LOOPS — remove bound package, re-scan, see what binds next, repeat
until no removable OEM package remains (capped at 6 passes). A single-install test
machine would never have shown this.

### Full reversible round-trip PROVEN on hardware
```
DFU+WinUSB(ours) --undo--> removed all 3 pkgs --> DFU unbound (Service=none)
  -> our diagnose flips to DfuNeedsFix "(none) instead of WinUSB"
  --fix--> WinUSB reinstalled --> DfuReady, DriverDesc "FC Driver Fixer (WinUSB)"
```
Bonus: the undo recreated a GENUINELY broken DFU state (couldn't before — old
ImpulseRC had pre-fixed the board), so the fix is now proven from a real broken state,
not just idempotently over an existing binding.

### Gotcha: PowerShell guard trips on `pnputil /enum-drivers`
A safety layer misparsed the `/enum-drivers` token as a Remove-Item path and aborted
the whole PS block. Verify driver state with Get-PnpDevice / registry instead when
scripting checks around the tool. (Does not affect the C# app, which calls pnputil via
ProcessStartInfo with ArgumentList.)

### RepairVcp: implemented, NOT yet hardware-tested
Testing it needs the VCP deliberately mis-bound (our tool refuses to do that by design),
so it'd need Zadig/manual to stage the damage. Reversible; test later if wanted.

## NAMED "Plug & Pray" + WPF REDESIGN + SECURITY PASS (2026-07-18)

### Name
**Plug & Pray** — ironic pun on Plug-and-Play, the Windows feature that's meant to make
this automatic and doesn't. Exe = `PlugAndPray.exe`. Driver stamped into Device Manager
= `Plug & Pray (WinUSB)`. Descriptive tagline carries discoverability since the name
is playful. In XAML the ampersand must be `&amp;`; in a WinForms Label set
`UseMnemonic=false` or it becomes an accelerator.

### SECURITY ASSESSMENT (findings + fixes)
1. **Root/TrustedPublisher certificate (inherent to libwdi).** Verified on hardware:
   `HasPrivateKey=False` (libwdi deletes the key after signing), subject scoped per
   device (`CN=USB\VID_xxxx&PID_yyyy (libwdi autogenerated)`), EKU = Code Signing only.
   So it validates exactly one driver catalog and can't sign anything else. Same as
   Zadig/ImpulseRC. MUST be disclosed in the README — undisclosed root certs destroy trust.
2. **FIXED — Undo left the cert behind.** `DriverStore.RemoveLibwdiCert()` now removes it
   from both LocalMachine\Root and \TrustedPublisher during Undo, so revert is complete.
3. **FIXED — DLL hijacking (the real vuln).** App runs elevated and loaded `libwdi.dll`
   via the normal search order, so a poisoned libwdi.dll beside the exe in a
   user-writable folder (Downloads) would load with admin rights = privilege escalation.
   `LibWdi`'s static ctor now installs a `NativeLibrary.SetDllImportResolver` that loads
   ONLY from `AppContext.BaseDirectory` via absolute path. Verified still working, and
   verified working inside a single-file bundle.
4. **Unsigned exe** — not a vuln but SmartScreen will scare beginners. Needs a
   code-signing cert before public release. STILL OPEN.
5. **No network code at all** (verified by source grep) — no telemetry/phone-home.
   Supply chain is clean: libwdi compiled from source by us; only NuGet dep is
   Microsoft's System.IO.Ports.

### Progress reporting (honest, not a fake marquee)
`Fixer.Progress` now carries `(Message, Step, Total)` with a `Percent` helper, and each
flow declares real steps: kick→re-enumerate→install→verify (4), install-only (2),
undo remove→rescan→cert (3). The UI animates between real step values. Deliberately NOT
an indeterminate marquee, and no fake "creep".

### WPF redesign (`app/FcDriverFixer.Wpf`, exe PlugAndPray.exe)
WinForms was fine but fights you for a modern look; WPF gives crisp vectors, gradients
and animation for far less effort. Core is reused untouched. Dark theme, gradient bg,
glowing status ring whose colour+glyph track state (✓ ready / ⚡ normal / ! fixable /
✕ none), gradient action button, slim gradient progress bar, custom title bar via
`WindowChrome`, footer trust line. Window 460x548.
**The old WinForms project (`FcDriverFixer.App`) was removed 2026-07-18; WPF is the only GUI.**

### Dev affordances
- `PlugAndPray.exe --preview:<State>` renders a representative card for any state
  (NormalMode / DfuNeedsFix / VcpDriverWrong / NothingDetected / DfuReady / working)
  so the design can be checked without staging real hardware conditions.
- `scripts/Capture-App.ps1 -Preview <State> -Out <png>` builds a screenshot loop.

### Screenshot gotchas (cost real time)
- `CopyFromScreen` grabs whatever is visually on top — it captured an unrelated window.
  Use `PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/)` instead.
- BUT `PrintWindow` returns BLANK for an **elevated** window from a non-elevated shell
  (UIPI). So: PrintWindow for the asInvoker iteration build; CopyFromScreen (foreground,
  unoccluded) for elevated builds.
- For fast UI iteration the manifest was temporarily set to `asInvoker`.
  **It is now restored to `requireAdministrator`** — never ship asInvoker.

### Packaging / optimization — `scripts/publish.ps1`
Self-contained single-file so pilots need NO .NET install (the audience can't get a USB
driver working; telling them to install a runtime first loses most of them).
- `PlugAndPray.exe` = **71.1 MB** one file; `fcfix.exe` = 36.2 MB.
- Flags: `--self-contained -p:PublishSingleFile -p:IncludeNativeLibrariesForSelfExtract
  -p:EnableCompressionInSingleFile -p:InvariantGlobalization -p:DebugType=none`.
- **Do NOT use PublishTrimmed** — unsupported for WPF, silently breaks XAML.
- Verified: libwdi loads correctly from inside the single-file bundle.

## README WRITTEN (2026-07-18)

`README.md` written for three audiences in order: panicked pilot (symptoms -> download ->
click), skeptical pilot (exactly what an admin tool changes), devs (build + licences).
Checked clean against the no-AI-tells rule: 0 em dashes, 0 en dashes, 0 " -- ", no
"not just X but Y". ~1570 words.

Key sections: symptom list for discoverability (the exact error strings people search),
the two-identities table explaining WHY it happens, an explicit "what it changes on your
PC" section, and honest Status/caveats (Win11 x64 only, no ARM64, not code signed,
AT32/GD32 deliberately off, VcpRepair untested).

**Certificate disclosure is prominent and honest**: scoped subject, Code Signing EKU only,
private key destroyed after signing, removed again by Undo. Explicitly notes Zadig and the
original ImpulseRC tool did the same without mentioning it.

### LGPL compliance — publish.ps1 now produces TWO artifacts
Bundling libwdi.dll inside a single-file exe prevents users replacing it, which LGPLv3
requires. So releases ship both:
- `dist/PlugAndPray.exe` (71.1 MB, single file, convenience)
- `dist/PlugAndPray-replaceable-libwdi.zip` (71.5 MB, libwdi.dll loose + replaceable)
The README promises this, so the script now warns loudly if libwdi.dll is missing from
the loose build. Vendored libwdi source + our exact build config stay in `vendor/libwdi`.

Credits section: ImpulseRC (original idea, independently rebuilt, nothing decompiled),
libwdi/Pete Batard (LGPLv3), Zadig (GPLv3, explicitly NOT copied from), Betaflight
(referenced for compatibility only, not affiliated).

**BLOCKED: `LICENSE` file does not exist yet** — README links to it. Needs a licence decision.

## CODE SIGNING RESEARCH (2026-07-18)

### The headline: no certificate buys instant trust any more
Microsoft's own docs (learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation):
> "EV certificates no longer bypass SmartScreen... this behavior no longer exists.
> Paying a premium for EV solely to avoid SmartScreen warnings is no longer justified."

Their table shows a validly signed app (OV **or** EV) still gets
"⚠️ Warning - app flagged as unrecognized until reputation accumulates". Signing buys
(a) a verified publisher name instead of "Unknown publisher", and (b) reputation that
accrues to the CERT so later releases inherit it. Nothing removes the day-one warning
except shipping via the Microsoft Store (impossible: driver installer, needs admin).
=> Cheapest credible option is now the CORRECT option, not a compromise.

**Reputation attaches to the certificate identity.** "Use a consistent signing identity -
changing your signing certificate affects the publisher trust signal." So this is
pick-one-and-commit, NOT try-free-then-upgrade.

**Windows 11 Smart App Control** can outright BLOCK unsigned executables (all executables,
not just downloads), so signing matters more than it did.

### Options assessed
| Option | Cost | Verdict |
|---|---|---|
| SignPath Foundation | **Free** | Best fit. GPLv3 qualifies. See constraint below. |
| Certum Open Source | ~$50-70 yr1 (incl. token), ~€29/yr | Viable. Cert in OUR name. |
| Azure Artifact Signing | $9.99/mo | **NZ NOT ELIGIBLE** (orgs: USA/Canada/EU/UK; individuals: USA/Canada only). Also needs a paid Azure sub. |
| Commercial OV/EV | $200-400+/yr | Pointless now, buys nothing extra. |
| Microsoft Store | free, zero warnings | Not viable for a driver installer. |

### SignPath: the big constraint (found late, important)
**Only CI-built artifacts can be signed. Locally built binaries CANNOT be.**
- "all jobs of the GitHub workflow leading up to the signing request must be executed on
  GitHub-hosted agents"
- SignPath verifies build origin, so a signature also proves the binary was built from the
  public source at the stated repo.
Also required (signpath.org/terms.html): OSI licence, no commercial dual-licensing,
actively maintained, **already released in the form to be signed**, functionality
described on the download page.
Publisher shown in Windows = **"SignPath Foundation"**, not the project/maintainer name.

**Implication for us:** we currently build locally. Using SignPath means porting the whole
build to GitHub Actions, INCLUDING compiling libwdi from source on a hosted runner
(fetch the WDK coinstaller MSI, apply our config.h/vcxproj/sln patches, build with MSVC,
then build .NET). All those patches are documented above, so it is automatable, but it is
real work. Upside: reproducible builds + cryptographic provenance, which is a genuinely
strong trust story for an elevated driver tool, and it serves the GPLv3 "this can never
die with one person" thesis.

### Certum: workable from NZ
Remote identity verification is accepted (photos of the subscriber holding their ID
document), so no travel to the EU. Also wants a utility bill and the URL of an active
open source project. Docs to ccp@certum.pl. Hardware smartcard + reader on first purchase
(cloud variant exists). NOTE: from 2026-03-01 max cert validity drops 39 months -> 460 days.

### Sequence if we go SignPath
public GitHub repo -> GitHub Actions build (incl. libwdi) -> unsigned v0.1 release ->
apply to SignPath Foundation -> signed releases thereafter.

## CI PIPELINE BUILT (2026-07-18)

Required because SignPath Foundation only signs **CI-built** artifacts (locally built
binaries cannot be signed at all), and because it removes the single-maintainer-machine
dependency that killed ImpulseRC.

### `scripts/Build-LibWdi.ps1` — reproducible native build
Builds libwdi from scratch on any machine, encoding all five landmines with inline
reasons. Pins upstream commit `30df0c0e051b0132c4b9ebed8c054bc8eb3aaaec`. Fetches the WDK
coinstaller MSI, extracts via `msiexec /a` (no elevation), patches, builds, then verifies
the output is a 64-bit PE. **Tested from a clean clone, works end to end.**
libwdi is now gitignored, NOT vendored in-tree: the script + pinned commit IS the
corresponding source, which is cleaner for licence separation.

### Bugs found while writing it (all silent-failure class)
1. **CRLF vs regex anchors.** `'^#define LIBUSB0_DIR ".*"$'` never matches, because the
   `\r` sits between the closing quote and the line end. Only the OPT_ARM rule worked, by
   luck (`\s` matches `\r`). Now all config/sln edits are done LINE BY LINE, not by
   multiline regex, and each rule asserts it matched something.
2. **vswhere needs `-products *`** or it silently ignores a Build Tools install and finds
   no MSBuild. (Same trap as earlier in the project.)
3. **Push-Location does not set the process cwd.** A native child process inherits
   `[Environment]::CurrentDirectory`, which PowerShell's location does not track, so a
   relative `libwdi.sln` resolved against the wrong directory. Now uses an absolute
   solution path AND sets `[Environment]::CurrentDirectory`.
4. **Solution builds insert the platform into the output path**
   (`bin/x64/Release/...`) whereas single-project builds do not (`bin/Release/...`).
   The CI smoke test now locates `fcfix.exe` instead of hardcoding it.
5. **`-notmatch` against a string ARRAY is a filter, not a boolean.** It returns the
   non-matching lines (truthy), so the CI assertion would have failed on EVERY run
   despite the expected text being present. Must `-join` to a single string first.

### `.github/workflows/build.yml`
checkout -> setup-dotnet 8 -> setup-msbuild -> cache WDK MSI -> Build-LibWdi.ps1 ->
`dotnet build` the solution -> **smoke test** (`fcfix list`, asserting
"WinUSB installable by this engine: True", which catches a libwdi built without WinUSB
support) -> publish.ps1 -> upload artifacts.
A second `sign` job is present but **inert** until repo vars/secrets are set
(`SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`,
secret `SIGNPATH_API_TOKEN`); it runs only on `v*` tags and drafts a release.

`scripts/publish.ps1` paths are now `$PSScriptRoot`-relative so the same script runs
locally and on a runner. Throwaway `*-test.cmd` scaffolding deleted (hardcoded paths,
superseded by `fcfix fix|undo`).

### PUBLISHED + CI GREEN (2026-07-18)
**https://github.com/humanzee-lab/plug-and-pray** (public, GPL-3.0 detected).
Initial commit `95ce9eb`, 25 files, no binaries in tree.

**CI passed on the FIRST run** (2m14s), including building libwdi from source on a clean
runner. Smoke test output confirms `libwdi.dll 3.7 MB x64` and
`WinUSB installable by this engine: True`, so WDK_DIR really was applied on the runner.
The `sign` job correctly skipped itself (no tag, no SignPath vars).

Gotcha: `gh repo create --source=.` pushed to **`master`**, but the workflow triggers on
`main`, so CI would never have fired. Renamed with `git branch -M main`, pushed, set the
default branch, deleted the remote `master`.

Known warning (non-blocking): the marketplace actions we use still target Node 20 and are
being force-run on Node 24. Bump action versions when upstream ships Node 24 builds.

Pre-publish scrub done: no personal identifiers, no `C:\Dev` paths, internal "Tom" voice
removed from NOTES.md, `dist-cli/` (36 MB) caught before it was committed, throwaway
`*-test.cmd` scripts deleted, superseded WinForms project removed.

### v0.1.0 RELEASED (2026-07-18)
https://github.com/humanzee-lab/plug-and-pray/releases/tag/v0.1.0 (public, not a draft)
Assets: `PlugAndPray.exe` 71.0 MB + `PlugAndPray-replaceable-libwdi.zip` 71.5 MB, both
built by CI on a clean runner.

Workflow gap found and fixed first: the release step lived INSIDE the signing job, so a
tag would have built artifacts but produced no GitHub Release at all. Now split into a
dedicated `release` job that runs on any `v*` tag, prefers signed artifacts and falls
back to unsigned with a log warning. It needs `if: always()` because a skipped `sign`
job would otherwise skip the release too.

Release notes deliberately lead with the two things a first-time user must know: the
SmartScreen warning (unsigned) and the certificate the tool installs and removes.

### NEXT: apply to SignPath Foundation
Their terms require the project to be **already released in the form to be signed**, so
cut a `v0.1.0` GitHub release (unsigned) FIRST, then apply. On approval set repo vars
`SIGNPATH_ORGANIZATION_ID` / `SIGNPATH_PROJECT_SLUG` / `SIGNPATH_SIGNING_POLICY_SLUG`
and secret `SIGNPATH_API_TOKEN`, and the existing `sign` job activates on the next tag.

## BUG: stale COM port on any PC with more than one board (fixed 2026-07-19)

Found the moment a SECOND flight controller was plugged in. Exactly the class of bug a
single test board can never reveal.

**Symptom:** app reported the board on COM6 and the fix failed. Windows had it on COM8.

**Cause:** `ComPortLocator.Find()` walked
`HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_0483&PID_5740` and returned the FIRST
subkey with a `PortName`. That key holds one instance for every device that has ever been
plugged in, each remembering its own port:

```
204639455333 -> COM6   (previous board, unplugged)
334E396B3230 -> COM8   (board actually connected)
```

So it returned a stale port belonging to a disconnected board, and the MSP reboot went to
a port that does not exist.

**Impact: would have broken for most users.** Any pilot who has ever connected two STM32
flight controllers to the same PC, which is close to all of them.

**Fix:** intersect the registry candidates with `SerialPort.GetPortNames()`, which only
returns live ports, and return null when nothing matches. Returning null is deliberate:
the caller then tells the user to use the BOOT button, which is far better than sending a
reboot command to the wrong port.

Verified: now reports COM8, full kick + install + verify passes on the second board
(instance `200364500000`, `Service=WinUSB`, `DriverDesc=Plug & Pray (WinUSB)`).

**Lesson for the test matrix:** anything derived from the PnP registry must be filtered
for presence. The registry is a history, not a description of what is plugged in now.

## Open decisions

- App licence (MIT vs GPLv3) — free choice as long as we only *link* libwdi
- Name — TBD
- Distribution / code signing — unsigned exe will get SmartScreen-flagged, which is a
  real adoption problem for a tool aimed at beginners

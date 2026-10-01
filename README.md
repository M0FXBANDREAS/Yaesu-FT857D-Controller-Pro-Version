# HamTech M0FXB Yaesu FT-857D Controller

Windows 10/11 desktop CAT controller adapted specifically for the Yaesu FT-857D.

## Normal users — use GitHub Releases
Do **not** use GitHub's green **Code → Download ZIP** as the normal installation package. That ZIP is the project source code.

Open the repository's **Releases** page, download `HamTech-M0FXB-FT857D-Controller-Windows-x64.zip` from **Assets**, extract it, then double-click `HamTech-M0FXB-FT857D-Controller.exe`.

The Release build is Windows x64 and self-contained, so normal users do **not** need to install the .NET SDK.

## Developers / Code → Download ZIP
The source ZIP can also be run on Windows 10/11. Extract the ZIP and double-click `START-HAMTECH-FT857D-CONTROLLER.cmd`. The launcher checks for the .NET 8 SDK, builds the project from the repository root, publishes a self-contained x64 executable into `publish`, and launches it.

For a full developer build/test/publish, run `build-windows.ps1` in PowerShell.

## Radio setup
Connect a suitable FT-857D TTL CAT interface to the radio's CAT/LINEAR jack. Set Menu 020 `[CAT/LIN/TUN]` to CAT, set Menu 019 `[CAT RATE]` to 4800, 9600 or 38400, then select the matching COM port and baud rate in the controller. CAT framing is 8 data bits, no parity, 2 stop bits.

## Supported documented CAT controls
- Read/set frequency (10 Hz CAT resolution)
- LSB, USB, CW, CWR, AM, WFM, FM, DIG and PKT modes
- VFO A/B toggle
- Split on/off
- Clarifier/RIT
- PTT press-and-hold
- RX S-meter/status polling
- Repeater shift and offset
- CTCSS encoder and tone
- 200 controller-side named memories

The FT-857D operating manual documents the five-byte CAT protocol used for these controls. Direct EEPROM/internal-memory access from the old FT-817/818 project is deliberately disabled because the FT-817/818 hard-coded EEPROM map is not documented for the FT-857D.

## Display / status features
- 16-segment LCD S-meter: S0-S9 illuminate green; S9+10 through +60 illuminate red.
- 200 controller-side named memories, shown as M-001 through M-200.
- PC squelch is an audio mute threshold driven by the CAT S-meter.
- RF Gain remains a radio-front-panel control because the documented FT-857D CAT opcode table does not provide a continuous RF Gain command.
- F7 TX-status polling switches the segmented meter between RX S-meter and TX PO indication and provides TX/RX, SPLIT and HI-SWR status.
- F11 toggles borderless full-screen mode; Esc restores the window.
- The orange TUNER button provides the FC-30/ATAS setup workflow; no undocumented tuner CAT opcode is sent.

## Automated GitHub build
`.github/workflows/windows-release.yml` builds and tests the project on a Windows GitHub runner. Manual workflow runs create a downloadable Actions artifact. Pushing a tag such as `v1.0.0` also creates/updates a GitHub Release and attaches the ready-to-run Windows x64 ZIP.

## Safety
Test in Demo mode first. PTT is press-and-hold. Verify antenna, frequency, mode, licence privileges and station configuration before transmitting.

Independent project; not affiliated with or endorsed by Yaesu. Yaesu and FT-857D are trademarks of their respective owner.


## PHOTO PANEL V3 - CAT LIVE

- The photographic COM-port and baud controls now feed the actual CAT connection directly.
- Opening the photo COM drop-down refreshes Windows serial ports.
- CONNECT performs a real FT-857D `03` frequency/mode query before reporting success.
- While connected, the controller polls documented frequency/mode, RX status and TX status and mirrors the available information on the photo panel.
- MODE, direct mode buttons, band buttons, tuning controls, A/B, split, clarifier, PTT, repeater shift/offset and CTCSS use documented CAT commands.
- Front-panel functions for which Yaesu publishes no CAT opcode (for example V/M, A=B, IPO, ATT, NB, AGC and DSP row functions) are deliberately not sent as guessed bytes; clicking those photo hotspots reports the CAT limitation.

Radio setup: rear CAT/LINEAR jack in CAT mode and CAT RATE matching 4800, 9600 or 38400 baud.

## V6 — TOP MENUS + CLARIFIER + LIVE METERS
- Visible top File / CAT / View / Memory / Settings / Help menus added over the photo panel.
- CAT menu includes connect/disconnect, read-now and PTT controls.
- Photo RIT/CLAR controls: mouse wheel changes documented CAT clarifier offset in 100 Hz steps; click toggles clarifier.
- XIT is intentionally not faked: the documented FT-857D CAT command set does not expose an independent XIT control.
- Bottom photo meters are now live. RX shows the documented CAT S-meter nibble. TX shows documented PO meter data and HI-SWR status.
- Existing working VFO, band, mode and CAT functions retained.

V7 fixes:
- Live CAT LCD moved into the actual FT-857D display window.
- Removed duplicate external menu bar.
- Replaced the non-working menu text baked into the photo with a real WPF menu in the same position.


## V8 alignment/meter fix
- Band selector overlay tightened and aligned with the photographic band panel.
- Photo-menu dropdown items use black text on a light menu background.
- RX S-meter and TX PO meter bars now animate directly on the printed bottom meter scales.
- Meter values use the FT-857D documented RX/TX status high nibble (0-15).


## V9 changes
- Visible CAT CONNECT/DISCONNECT button now changes both label and colour with connection state.
- Added visible STEP button; cycles the existing 10/100/1k/5k/10k/100k Hz PC tuning steps.
- Live CAT S-meter now drives both the radio LCD bar and the lower RX meter overlay.
- TX CAT PO data drives the lower TX meter; HI-SWR remains a documented binary warning.
- FT-857D documented CAT does not expose continuous ALC/SWR values or direct NB/NR/DNF/DMR controls, so V9 does not invent unsafe/undocumented commands.

## V10 — Live Audio Scope + Green Meter Selector
- Replaces the photographic FILTERS/DSP button bank with a live Windows audio-input oscilloscope using NAudio.
- Green METER button cycles SIGNAL / POWER / SWR / ALC.
- SIGNAL uses the same FT-857D CAT S-meter value as the LCD S-meter, so the bottom photo meters are synchronized.
- POWER uses the documented CAT PO nibble.
- SWR uses the documented HI-SWR flag (the FT-857D CAT protocol does not provide a continuous SWR value).
- ALC is shown as N/A via CAT because the documented FT-857D CAT status does not provide an ALC meter value.
- Both bottom photographic meter needles/bars follow the currently selected meter source.
- Corrected scan S-meter extraction to use the same high nibble as normal RX polling and corrected squelch-status bit handling.


## V11 — Live mode sync
- The photographic LSB/USB/AM/FM/DIG bank now follows the same `_mode` state used by the CAT frequency/mode poll.
- Changing mode on the physical FT-857D updates the illuminated photo-panel mode on the next CAT poll.
- Clicking a photo mode button still sends the documented CAT Set Mode command and updates the same indicator state.
- FM/WFM are represented by the single FM lamp available on this artwork. CW/CWR/PKT still appear on the live LCD mode text but have no dedicated lamp in this five-button artwork.

## V12 panel alignment / UI polish
- Tightened all five live mode illumination overlays so they sit inside the photographed LSB/USB/AM/FM/DIG button faces.
- Moved and resized the red PTT control into the VFO/MEMORY lower panel so it no longer floats across panel boundaries.
- Moved/resized the green METER selector and STEP control into a clean vertical control strip beside the meters.
- CAT, mode sync, VFO, bands, scope, meter logic and serial protocol are unchanged from V11.

## V13 — Gridded CAT signal meters
- Corrected FT-857D RX/TX status-byte decoding to the documented bit layout.
- CAT S-meter and PO fields are the documented bits 3..1 (0..7), scaled to the 16-segment UI.
- LCD S-meter is now a 16-cell grid: green at weaker levels and red at strong levels.
- Bottom-left analogue meter is permanently synchronized to the same live RX S-meter value as the LCD.
- Bottom-right analogue meter remains controlled by the green SIGNAL/POWER/SWR/ALC selector.


## V14 — interactive frequency display
- Click the large amber frequency display, type a frequency, and press Enter to send it to the FT-857D.
- Escape cancels editing.
- Mouse wheel over the frequency display tunes up/down using the selected STEP value.
- The display remains synchronized with CAT polling and the main VFO control.

## V15 meter recovery + clarifier fix
- Live CAT polling no longer stops permanently after one missed E7/F7/status reply; the next timer tick retries automatically.
- LCD gridded S-meter and bottom-left analogue SIGNAL meter remain driven from the same decoded RX S-meter value.
- On a real CAT connection, CLAR is explicitly switched OFF once so the application and radio start in a known state.
- Changing the RIT/CLAR offset with the wheel or +/- controls no longer silently turns CLAR ON. CLAR is enabled only with its explicit toggle.

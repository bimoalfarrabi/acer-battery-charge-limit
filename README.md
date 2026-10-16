# acer-battery-charge-limit

Control the Acer **Battery Charge Limit** (80% "health mode") from the command line --
turn it on/off and read its state **without** the buggy Acer Care Center UI.

Acer Care Center sometimes hangs at *"still initializing"* (notably after hibernate/resume),
which makes the battery-limit toggle unusable. The limit itself is a firmware/EC flag exposed
over WMI, so we can drive it directly and skip the UI entirely.

```
> battctl status
Battery Charge Limit: ON  (charge limited to 80%)

> battctl off
Battery Charge Limit: set -> OFF (charges to 100%)
```

## Requirements

- Windows 10/11, an Acer laptop whose firmware exposes the WMI charge-limit interface
- Administrator rights **only for the one-time install** (afterwards: no UAC prompts)

Verified on **Acer Nitro AN515-57** (BIOS V1.22). Likely works on many modern Acer
Aspire / Swift / Nitro / Predator models -- see [Supported models](#supported-models).

## Install

Clone (or download) this repo, then run the installer once:

```powershell
git clone https://github.com/bimoalfarrabi/acer-battery-charge-limit
cd acer-battery-charge-limit
.\battctl-install.cmd        # one UAC prompt
```

The installer:

- copies the worker and front end to `C:\Program Files\battctl\` (the worker is admin-only, so `SYSTEM` can run it safely),
- creates the mailbox `C:\ProgramData\battctl\mailbox\` and grants *you* modify access,
- registers the on-demand scheduled task **`BattCtl`** (runs as `SYSTEM`, highest privileges, 60 s cap),
- registers the boot/resume task **`BattCtlResume`**, which re-applies the saved state,
- adds `C:\Program Files\battctl` to the system `PATH`.

After installation, `battctl` is available in any **new** terminal (already-open shells keep the old
`PATH`). To update after `git pull`, re-run `.\battctl-install.cmd`. To run `battctl` from **WSL**,
see [WSL usage](#wsl-usage).

## Usage

```powershell
battctl status     # show current state          (default)
battctl on         # enable the 80% charge limit
battctl off        # disable it (charge to 100%)
battctl toggle     # flip the current state
```

`battctl on` and `battctl off` remember your choice in
`C:\ProgramData\battctl\desired.txt`. The saved state is re-applied automatically after
hibernate or sleep and at boot. For troubleshooting the re-assert, see
`C:\ProgramData\battctl\restore.log`.

Exit codes:

| Code | Meaning |
|------|---------|
| `0`  | success |
| `1`  | WMI/provider failure |
| `2`  | feature not present on this model (no `BatteryControl` instance) |
| `3`  | timed out waiting for the task (WMI provider stuck -- `schtasks /end /tn BattCtl`) |
| `4`  | bad argument, or the tool is not installed |

## How it works

The limit is a firmware flag reachable through WMI:

- namespace `root\WMI`, class **`BatteryControl`** (ACPI instance `ACPI\PNP0C14\APGe_0`)
- read : `GetBatteryHealthControlStatus(uBatteryNo=1, uFunctionQuery=1, ...)`
  -> `.uFunctionStatus[0]` = `1` (ON / 80%) or `0` (OFF / 100%)
- write: `SetBatteryHealthControl(uBatteryNo=1, uFunctionMask=1, uFunctionStatus=0|1, ...)`

The WMI instance is **only visible to an elevated caller**, which is why the work runs inside a
`SYSTEM` scheduled task rather than prompting for UAC on every call. The front end drops a
nonce-tagged request into a mailbox file, triggers the task, waits for the matching result and
prints it.

```
you -> battctl.ps1 -> request.txt -> [BattCtl task / SYSTEM] -> WMI BatteryControl
                              ^                                      |
                              +------------ result.txt <-------------+
```

The worker is pinned to **Windows PowerShell 5.1** (`powershell.exe`), because
`Get-WmiObject` -- the call verified to work with this provider -- does not exist in PowerShell 7.

## Supported models

If `battctl status` exits `2`, your firmware does not expose `BatteryControl`. Known-working
Acer families (from the Linux [`acer-wmi-battery`](https://github.com/frederik-h/acer-wmi-battery)
driver, which talks to the same interface) include Aspire 3/5/7, Swift 3, Nitro AN515-x and
some Predator models. Reports for other models are welcome.

## Caveats

- The flag lives in EC RAM and may be **lost on a full battery drain / EC reset** -- just run
  `battctl on` again.
- Repeated toggling is safe: it's an EC RAM write, not a flash write. The tool is idempotent.
- Safe to use while Care Center is stuck *"still initializing"*: the WMI provider is served by the
  service stack (`ACCSvc.exe`), not the hung UI process (`ACCStd.exe`).

## Known limitations

- The charge-limit flag **is lost after hibernate on the tested machine**. The installer registers
  the `BattCtlResume` task, which re-applies your last chosen state after resume and at boot.

## Uninstall

```powershell
.\uninstall.cmd        # one UAC prompt; removes the task, worker and mailbox
```

## WSL usage

A wrapper is included at [`wsl/battctl`](wsl/battctl). It calls the fixed install path
`C:\Program Files\battctl\battctl.cmd`, so it needs no editing:

```bash
cp wsl/battctl ~/.local/bin/battctl && chmod +x ~/.local/bin/battctl
battctl status
```

(UAC prompts are invisible from a WSL terminal, which is why the tool uses a `SYSTEM` task
instead of self-elevation -- see [How it works](#how-it-works).)

## Credits

Reverse-engineered from these projects, which use the same WMI interface:

- [rafradek/Acer-Predator-Scripts](https://github.com/rafradek/Acer-Predator-Scripts)
- [supesonly/Acer-P-Helper](https://github.com/supesonly/Acer-P-Helper)
- [frederik-h/acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery) (Linux driver)

## License

[MIT](LICENSE)

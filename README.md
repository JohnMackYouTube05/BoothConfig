# Booth Config

A Windows program for changing the configuration of Foto Fantasy based photo booths. It is a graphical
alternative to the text-mode `nsyscfg` utility.

## Use

1. Download `BoothConfig.exe` from the Releases page. It needs .NET Framework 4.8, which is part of
   Windows 10 (1903 and later) and Windows 11.
2. Put it in the booth's data folder (usually `vend`) and run it, or run it from anywhere and choose
   **Open booth folder…**. A folder can also be given on the command line.

The booth folder is the one containing `nsyscfg.ini`. If the program is in or below such a folder, or is
started from one, it loads that booth right away.

## How it works

| File | Used for |
| --- | --- |
| `nsyscfg.ini` | The forms, fields, menus and help to show |
| `locale.ini` | Currency sets, price lists and languages |
| `vsystem.cfg` | The saved values |
| `vsystem.alt` | Kept identical to `vsystem.cfg` |
| `zsystem.cfg` | Defaults for values that are not set |

Choosing a currency set or vend type also updates the prices and the coin, bill and token values that
depend on it. Choosing a printer updates the printer type.

Saving lists the changes first, writes only the lines that changed, keeps the read-only attribute, and
puts the previous file in `BoothConfig-backups`.

## Limits

- Scripts that `nsyscfg.ini` attaches to a field (such as `fix-vsyscfg.sh` for the control panel style)
  are not run.
- Changes to the daily service hours are not logged in `norms.log`.
- Only tested with copies of one booth's data so far.

## Build

With the .NET SDK and the .NET Framework 4.8 developer pack:

    dotnet build -c Release

This project does not include any booth software or data.

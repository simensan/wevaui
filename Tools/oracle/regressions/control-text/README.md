# Form-control text defaults and indentation

Five fixtures match Chrome with the normalized mono-font harness. Controls reset
inherited letter/word spacing, text transform, indentation and shadow; authors can
explicitly inherit them. Inline offsets apply once, and shrink-to-fit sizing
includes the first-line indent. Cases include positive, negative and percentage
indentation and a float inset.

```powershell
node Tools/Layout/capture-all-chrome-layouts.mjs Tools/oracle/regressions/control-text 800 600 --metrics=mono
python Tools/oracle/chrome_sweep.py Tools/oracle/regressions/control-text --weva-dump <weva_dump.exe> --width 800 --height 600 --chrome-metrics --out-dir .utmp/control-text
```

Evidence: `.utmp/parity68/control-indent-fixtures2`. Core tests cover control
cascade overrides and rendered-position/size assertions for indentation.

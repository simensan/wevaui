# Known gaps

Cases that do **not** gate, because they fail for a reason no amount of
porting fixes: the C++ engine and the C# reference agree with each other and
both differ from a browser. The oracle's job is parity, and on these two the
parity is already there — what is missing is the feature, in both engines at
once.

They live here so the finding is not lost. Run one the same way as any other
case; the numbers below say what to expect.

    cp known-gaps/cov-table.* corpus/samples/
    (cd ../../../Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/samples 1280 720 --metrics=mono)
    python3 run_oracle.py corpus/samples --width 1280 --height 720 \
        --weva-dump <build>/tools/weva_dump/weva_dump --reuse-reference

## cov-table — a table is not shrink-to-fit

`display: table` takes a shrink-to-fit width, like a float or an inline-block.
Both engines give it the containing block's full width instead.

Measured at a 792px page, 16px padding:

| box | C++ | C# reference | Chrome |
|---|---|---|---|
| `table.collapse` w | 760 | 760 | **398.5** |
| first `th` w | 252.7 | 252.7 | **313.1** |
| second `th` w | 252.7 | 252.7 | **35.9** |

The column widths follow from it: with 760px to divide the three columns come
out equal, and Chrome sizes each to its content — a wide first column, a
narrow one for `qty`. The first 26 boxes of the case are IDENTICAL between the
two engines and all 26 differ from Chrome, which is the signature of a missing
feature rather than a porting mistake.

Closing it means implementing the CSS table auto-layout width algorithm
(min/max content widths per column, then distribution), and it will make the
port disagree with the reference until the reference gets it too. That is a
decision about which engine leads, not a bug to fix quietly, which is the
other reason this case is parked rather than deleted.

A second, smaller thing the case pins: the reference places a table's
following sibling 244px too low (`table.separate` at y=440 against Chrome's
174), where the port says 196. So the port is already the better of the two
here — it is only Chrome's tolerance it misses.

Nothing else in the case disagrees. `border-collapse`, `border-spacing`,
`caption-side`, `empty-cells`, `table-layout: fixed`, `colspan` and `rowspan`
all produce the same boxes in both engines.

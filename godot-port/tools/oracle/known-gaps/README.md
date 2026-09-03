# Known gaps

Cases that do **not** gate, because they fail for a reason no amount of
porting fixes: the C++ engine and the C# reference agree with each other and
both differ from a browser. The oracle's job is parity, and on these two the
parity is already there — what is missing is the feature, in both engines at
once.

They live here so the finding is not lost. Run one the same way as any other
case; the numbers below say what to expect.

    cp known-gaps/cov-table.* corpus/samples/       # or any case here
    (cd ../../../Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/samples 1280 720 --metrics=mono)
    python3 run_oracle.py corpus/samples --width 1280 --height 720 \
        --weva-dump <build>/tools/weva_dump/weva_dump --reuse-reference

## cov-gutter — scrollbar-gutter reserves nothing

Neither engine reads `scrollbar-gutter`. Both leave the content box at its
full width; Chrome holds the scrollbar's width open inside it.

Measured through a block child, because the property never changes the scroll
container's own box — only the space left inside it. (The first version of
this case had no such child and therefore measured nothing at all, while
appearing to pass.)

| pane | child width, C++ and C# | Chrome |
|---|---|---|
| `overflow-y: auto`, no gutter | 210 | 210 |
| `scrollbar-gutter: stable` | 210 | **195** |
| `stable both-edges` | 210 | **180**, and shifted +15 |

Note this case PASSES the oracle: the two engines agree with each other
exactly. That is why it is filed here rather than left in the gating corpus —
a green `cov-gutter` would read as "scrollbar-gutter works".

Closing it needs a scrollbar width to reserve, and that is the catch. Chrome's
15px is its classic scrollbar; this engine draws a 7px overlay one. Reserving
7px would honour the property and still not match Chrome, turning a case that
passes into one that fails. So the width has to be decided first — the same
decision as the form-control defaults above.

## cov-field-sizing — form-control intrinsic widths

`field-sizing: content` makes an input take the width of the value it holds
instead of the UA's fixed default. The port ignores it entirely; the reference
implements something, but not what a browser does.

And the disagreement starts one step earlier, which is why this is parked
rather than fixed: the DEFAULT width of an unstyled `<input type=text>` is
different in all three engines.

| input | C++ | C# reference | Chrome |
|---|---|---|---|
| no `field-sizing` | 102.7 | 161.8 | **143** |
| `content`, value `"ab"` | 102.7 | 16.8 | **45.25** |
| `content`, long value | 102.7 | 185.1 | **218** |
| `content` + `width: 160px` | 75.3 | 16.8 | **34.5** |

The port's column is constant, which is the signature of the property being
unread. But fixing only that would leave the default wrong, and the default is
a UA-stylesheet number that the whole form corpus is calibrated against —
`form-demo`, `forms-live`, `inputtest` and `form-metrics` all agree with the
reference today and would move. It is the same open question as the unstyled
`<button>` box, and it wants deciding once, for every control, against Chrome.

The last row is worth its own note: an explicit `width` must win over content
sizing, and Chrome's 34.5 says it does not simply win — the port's 75.3 is not
160 either. Whatever replaces this should pin that case deliberately.

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

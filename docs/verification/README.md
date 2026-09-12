# Verification receipts

Each JSON file here records one verification run: what was checked, against
which build, and what the answer was. They are cited from
[`PRODUCT_READINESS.md`](../PRODUCT_READINESS.md) and the readiness history,
and they are the reason a claim in those documents can be traced to a run
rather than taken on trust.

**Receipts are evidence. They are not rewritten, tidied, or regenerated** — not
to correct a path, not to shrink a file, not when a later run disagrees. A
receipt that disagrees with today's build is the useful kind. The paths inside
them, absolute Windows ones included, say where the evidence was produced; the
`godot-port/` prefixes predate the flatten and are correct for the runs they
describe.

## What a new receipt should contain

The directory is 229 files and 16 MB in a checkout. That sounds worse than it
is — JSON compresses about thirteen to one, so it is **1.2 MB packed**, under
one percent of the repository. Size is not the problem. The shape is.

The tail is heavy for one reason: a few receipts embedded their raw inputs
instead of their findings. `binding-commit.json` is 2.1 MB, and 265 KB of that
is a `runs` array of per-frame timings — sitting directly beside the `medians`
computed from it. The medians are the evidence. The frame array is an input the
benchmark regenerates on demand.

So, when writing a new receipt:

* **Keep** the verdict, the per-check results, the build identity (SHAs of the
  library, executable and sources), the environment, and the summary statistics
  a conclusion rests on — medians, percentiles, budgets, counts.
* **Keep** anything a later reader would need to tell a pass from a fluke: the
  thresholds, the sample size, the timeout, what was rejected.
* **Leave out** the raw arrays those statistics were computed from, and any
  payload the tool can produce again from the same inputs. Name the command
  that regenerates it instead.

A receipt should read as an answer with its working shown, not as a log file
with an answer somewhere inside it.

## Existing receipts

Unchanged, and staying that way. The rule above applies to what gets written
next; applying it backwards would mean editing evidence to make it prettier,
which is the one thing this directory is for not doing.

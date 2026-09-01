# Port plan

Bottom-up, oracle-gated. Each phase ends with a diff-clean corpus subset; no
phase starts before the previous one is green.

Sizes are the C# source being translated, from the feasibility measurements.
They are *scope indicators*, not schedule estimates — see "Effort" at the end.

---

## Phase 0 — Foundations (no engine code)

* Fix and verify the stale csproj excludes; confirm `BaselineGen` builds and
  runs headlessly (`dotnet build`, then a dump of `randhtml.html`). **The oracle
  must work before anything depends on it.**
* CMake skeleton: `libweva` (static), `weva_dump` (CLI), `hosts/godot` stub
  loading in Godot and printing its ABI version.
* Lock CONVENTIONS.md: `-fno-exceptions`, no fast-math, arena allocator,
  interning table, `Status` enum.
* Build `tools/oracle/` — corpus harvester, diff runner, CI wiring.

**Exit:** an empty C++ engine produces an empty dump, the diff runner reports
"412 elements expected, 0 produced" against a real corpus entry, and CI runs it.

**Status: done, with one item unverified.** CMake skeleton builds clean under
gcc 13 and clang 18; `Arena` / `SymbolTable` / `Status` land with 17 checks
green, also under ASan+UBSan; `weva_dump` emits BaselineGen's format exactly;
`diff.py` self-tests against identical, off-by-0.01, and wrong-identity inputs;
`harvest.py` pulls **3,879 corpus entries** partitioned by feature (block 1563,
cascade 967, flex 450, grid 269, scrolling 169, positioning 159, inline 121,
text 113, multicol 46, tables 22).

Two things worth carrying forward:

* The csproj exclude fix is **applied but unverified** — this container has no
  .NET SDK, so `BaselineGen` has not actually been built. Phase 1 must not
  start until someone runs `dotnet build` on it. There is no oracle until then,
  and `run.sh` exits 2 rather than reporting a vacuous pass over zero entries.
* Two bugs were caught during the phase and are worth remembering as a class.
  `SymbolTable` originally stored `std::vector<std::string>`, whose reallocation
  moves SSO character data and dangles every `string_view` key in the index —
  precisely the lifetime hazard CONVENTIONS.md warns about, hit on the first
  file that could hit it. And `weva_dump` initially formatted with `%.2f`,
  which rounds half-to-even in glibc where C#'s `Round2` rounds away from zero;
  0.125, 2.675 and 16.005 all diverge. Both are now covered by tests.

## Phase 1 — Core infrastructure (~5k LOC)

Arena allocator, string interning, `Status`, the value types Weva's paint layer
already owns host-agnostically (`Rect`, `Transform2D`, `LinearColor`,
`BorderRadii`), and the DOM (`Runtime/Dom`, 420 LOC — small and self-contained).

**Exit:** DOM construction from a hardcoded tree; unit tests on arenas and
interning; zero leaks under ASan.

**Status: done.** 88 checks green under gcc 13, clang 18 (Release, `-Wall
-Wextra -Wpedantic`, no warnings) and ASan+UBSan with LeakSanitizer verified
active. Landed: `Rect` / `Transform2D` / `CornerRadius` / `BorderRadii`
(`Paint/*.cs`), `LinearColor` + the sRGB curve, `RefCounted`/`Ref`, and the DOM
(`Node`, `Element`, `TextNode`, `Document`, `AttributeMap`).

Fidelity notes worth keeping:

* **Field widths were copied, not normalised.** `Rect` is double; `Transform2D`
  holds floats with a double `apply()`. Tidying either to a uniform type would
  surface later as unexplained sub-pixel divergence in the oracle.
* **`srgb_byte_to_linear` matches C#'s exponent exactly.** C# writes
  `Math.Pow(x, 2.4f)`; `Math.Pow` has no float overload, so the literal widens
  to `(double)2.4f` = 2.400000095367431640625, not 2.4. A bare `2.4` here gives
  a scatter of one-ULP color differences that read as a cascade bug. Same class
  of trap as Phase 0's `%.2f` rounding.
* **No RTTI.** `dynamic_cast` in the tree walks was replaced with an explicit
  `NodeType` tag, per CONVENTIONS.md — cheaper on a hot traversal and keeps the
  core RTTI-free.
* **Ownership.** Parent holds `Ref` to children, child holds a raw parent
  pointer, so a well-formed tree has no cycles. `append_child` and
  `remove_child` retain across the unlink, because erasing drops the parent's
  only reference and observers still need a live target.

C# semantics preserved deliberately, each with a test: re-appending the current
last child is a no-op *including the version counter*; `remove_child` fires its
mutation **before** unlinking so the parent chain is intact for bubbling;
detached subtrees report a null owner document; mutations bubble to every
ancestor with `target` always the originally mutated node; no-op attribute
writes bump nothing.

## Phase 2 — HTML + CSS parsing (~7k LOC)

`Runtime/Parsing` (1,100) + `Runtime/Css/Parsing` (~2k) + `Runtime/Css/Values`
(`CssValueParser` alone is 1,866). This is where the `string_view` lifetime rule
gets its first real test.

**Exit:** property round-trip dumps diff clean. Every value type the C# parser
accepts parses identically, including the `from_chars` locale behaviour.

**Status: HTML tokenizer done; HTML tree builder and all CSS parsing remain.**
152 checks green under gcc 13, clang 18 and ASan+UBSan+LSan. Tokenizes the
17KB dev demo (`Assets/UI/randhtml.html`) into 1,177 tokens / 27 interned names.

**The encoding change is the substance of this phase.** C# tokenizes UTF-16
`char`s; C++ tokenizes UTF-8 bytes. Three consequences, each deliberate:

* **Whitespace classification is Unicode, not ASCII.** `char.IsWhiteSpace`
  decides where an unquoted attribute value ends, so `is_unicode_whitespace`
  reproduces its BMP set exactly. Narrowing it to ASCII would silently swallow
  a U+00A0 separator into the value — there is a test with a real U+00A0 in it.
  Note U+200B is *not* whitespace to `char.IsWhiteSpace`, and isn't here either.
* **Line/column count code points, C# counts UTF-16 units.** An astral
  character reports one column less. Columns appear only in diagnostics, never
  in a layout dump, so the oracle is unaffected.
* **Surrogate numeric entities are rejected as literal text** (`&#xD800;`).
  C# calls `char.ConvertFromUtf32`, which *throws* — an unhandled
  `ArgumentOutOfRangeException` escaping the tokenizer. This is a deliberate
  deviation, matching browsers and almost certainly what the C# meant.

Tag and attribute names are interned (`Symbol`) rather than held as strings.
Selector matching and the void/optional-close tables compare them constantly.

Also worth noting: the C# comment terminator bound is `pos + 2 < length`, not
`<=`, so a comment ending at the final byte of the buffer reads as
unterminated. Reproduced rather than fixed — the oracle compares against C#
behaviour, and a unilateral fix here is a divergence.

**Tree builder done** (`HtmlParser.cs`, 645 LOC): fragment normalization, the
optional-tag implicit closes, optional-close scope guards, and the AAA-lite
active-formatting-list reconstruction. 193 checks green across gcc / clang /
ASan+UBSan+LSan; the 17KB dev demo parses clean in **strict** mode into 358
elements and 458 text nodes.

### Two places where the C# comments claimed behaviour the code did not deliver

Both were found by porting. Decisions taken:

1. **A body-only fragment got no `<head>` — FIXED in both engines.**
   `EnsureHead()` was only reachable from a head-content element, and
   `EnsureBody() -> CloseHead()` returned immediately when `inHead` was false,
   so `<main>hi</main>` produced `html(body(main))`. Layout was unaffected
   (both stated reasons for wrapper synthesis — `:root` matching and
   html/body background propagation — work either way), but **structural
   selectors diverged from Chrome**: `<body>` was `:nth-child(1)` instead of
   `:nth-child(2)`, breaking `body:nth-child(2)`, `html > *:first-child` and
   top-level sibling combinators.

   `HtmlParser.cs`'s `EnsureBody()` now calls `EnsureHead()` first, and the C++
   mirrors it. `HtmlFragmentWrapperTests` already tolerated both shapes
   ("head is empty so we allow either [body] or [head, body]"), so no C# test
   needed changing — **though the C# side is unverified here: no .NET SDK.**
   The C++ regression test asserts child *positions*, not just the shape, since
   a shape assertion alone would not catch `<head>` being emitted after
   `<body>`.

2. **The adoption-agency fixup does not match Chrome — DEFERRED, deliberately.**
   The comment cites `<p>Click <a><div>here</div></a> to start</p>` as
   "matching the Chrome / Firefox DOM shape". Chrome produces
   `<p>Click <a></a></p> <a><div>here</div></a> <a> to start</a> <p></p>`.
   The C# instead nests the reconstructed `<a>` *inside* the `<div>` and leaves
   `" to start"` unwrapped, because `</a>` clears the active formatting list
   before the trailing text arrives. That is layout-visible — the trailing text
   loses its link styling.

   Fixing it means implementing real HTML5 §13.2.6 adoption agency, which is a
   project rather than a patch, and doing it mid-port would blind the
   differential signal across every formatting-element corpus entry. Reproduced
   exactly for now; revisit once the corpus is green, then fix both engines
   together with the oracle watching.

**CSS tokenizer done** (`CssTokenizer.cs`, 452 LOC). 274 checks green across
gcc / clang / ASan+UBSan+LSan; the demo stylesheet tokenizes clean in strict
mode into 5,452 tokens (983 ident, 348 dimension, 180 function, 43 hash).

* **`std::from_chars` refuses a leading `+`** — it only recognises one in an
  exponent — where C#'s `NumberStyles.Float` accepts it. The tokenizer feeds it
  raw source text, so unhandled this would silently turn `margin: +5px` into
  `margin: 0`. `css_parse_double` strips the sign first and is tested directly,
  including the failure cases (`.`, `-`, `+`, empty) that must yield 0 to match
  `TryParse`'s out-param default.
* **This file needs no UTF-8 decoding, and that is a decision.** CSS Syntax
  §4.2 makes every non-ASCII code point a name-start code point (`c >= 0x80` in
  the C#), and every UTF-8 continuation byte is also `>= 0x80` — so byte-wise
  scanning produces identical identifiers. Unlike the HTML tokenizer, which
  genuinely needed code points.
* **CSS whitespace is ASCII-only**, deliberately narrower than the HTML
  tokenizer's `char.IsWhiteSpace`. The two must not be unified.

**Process note, now with a pattern.** Across the HTML parser and CSS tokenizer,
**11 test expectations were wrong and zero port bugs were found.** Every failure
was me asserting from recall — Chrome's DOM shape, or what "looked right" — 
rather than from the reference. Three worth keeping:

* `c-->d` tokenizes as `Ident("c--") Delim(">") Ident("d")`, not a CDC: `-` is
  a name code point, so the ident absorbs both dashes. CDC only applies at a
  token boundary.
* A skipped CDC emits nothing, so the whitespace either side survives as **two
  adjacent Whitespace tokens**. Downstream consumers must tolerate that.
* The single whitespace after an escape's hex digits is the escape terminator
  and is consumed; a second space is literal content.

For a differential port, "what does the reference actually do" is a question to
answer by reading or running it, never by recall.

**CSS parser core done** (`CssParser.cs`, the style-rule / declaration path).
349 checks green across gcc / clang / ASan+UBSan+LSan; the demo stylesheet
parses clean in strict mode into 126 top-level rules, 128 style rules,
4 at-rules and 485 declarations.

Declaration values are **reconstructed from tokens**, not sliced from source
(`css_token_source`), so any drift there changes every value the cascade sees —
it is tested directly. `!important` uses the last top-level `!`, because the C#
loop overwrites its candidate rather than breaking, so `1px !x !important`
keeps `!x` in the value.

Deferred, not lost: the specialised at-rules (`@font-face`, `@property`,
`@scope`, `@layer`, `@keyframes`, `@import`) land as `GenericAtRule` with
prelude and body preserved, so they can be promoted to real types without
re-parsing. `NestingExpander` is also not yet ported — the nested-rule tree is
kept as parsed.

### A third C#/Chrome divergence, same handling

**An empty declaration value swallows the following declaration.**
`TryParseDeclaration` consumes the terminating `;` *before* it checks
`sawNonWs`, so when it then returns false the caller's `SkipDeclaration` starts
after that semicolon and runs to the next one. `a{color:;margin:0}` therefore
drops **both** declarations; Chrome keeps `margin:0`. An empty value at the end
of a block loses only itself, which is why this is easy to miss.

Reproduced rather than fixed, per the standing rule. Add to the same review as
the adoption-agency divergence.

**CSS value model done** (`CssValue`, `CssLength`, `CssAngle`, hex `CssColor`,
`LengthContext`, and the `ParseTopLevel`/`ParseSingle` path). 446 checks green
across gcc / clang / ASan+UBSan+LSan. **477 of the 485 declaration values in the
demo stylesheet parse**; the 8 failures are `fr` and `s` units, which
`CssLength.TryParseUnit` genuinely does not accept in the C# either —
CONFORMANCE.md records that `fr` "is accepted only inside grid track lists", and
durations are read by the animation code from raw text. Faithful, not a gap.

**A whole cluster of C# machinery is deliberately not ported**, and it is the
clearest example so far of the port paying for itself: `CssValue`'s process-
lifetime parse cache, its negative-result cache, `CssValuePool`, and
`CssValueStableCopy`. The last of those exists *only* because pool-rented
leaves would otherwise be mutated underneath the cache — "the same `300px` key
would later return a `CssLength` carrying whatever number the pool re-used the
slot for". With arena allocation the values are bump-allocated per pass and
dropped wholesale, so the cache, the pool, the stable-copy pass and the
lifetime hazard all disappear together.

Reproduced rather than corrected: `ch`/`ex`/`cap`/`ic` are font-size
approximations in the C# (0.5x, 0.5x, 0.7x, 1.0x) rather than real font
metrics. Revisit when the text stack can supply true values — in both engines.

**Colours done.** 498 checks green. The named table has **168 entries**
(not the ~148 I assumed) including the CSS system colours, and was extracted
mechanically from `CssNamedColors.cs` rather than retyped — a transcription slip
in a colour table is invisible until someone notices the wrong shade on screen.
`rgb()`/`rgba()`/`hsl()`/`hsla()`/`hwb()` collapse to `CssColor` during parsing;
anything whose arguments don't evaluate (a `var()` inside, wrong arity) stays a
`CssFunctionCall` for a later pass.

**Two rounding traps, in opposite directions, in the same C# codebase:**

* `CssColor.ChannelByte` uses **parameterless `Math.Round`**, which is banker's
  rounding (`ToEven`). `std::round` is away-from-zero and would put every
  midpoint channel one off — so this uses `std::nearbyint`.
* `BaselineGen`'s `Round2` is explicitly **`MidpointRounding.AwayFromZero`**,
  which is why `weva_dump` needed the opposite fix in Phase 0.

Picking the wrong one is silent in both directions. Worth checking the mode at
every `Math.Round` call site for the rest of the port.

A third, subtler one: `50% -> 50 * 2.55` is `127.49999999999998`, **not**
`127.5`, because 2.55 has no exact double representation. It rounds down under
either mode, and C# does the identical multiply. The test says so explicitly,
because it looks exactly like an off-by-one someone would "fix".

Also reproduced: C#'s `FromRgb` takes a single `rgbPercent` flag for all three
channels, so a mixed `rgb(255, 50%, 0)` follows whatever the *first* channel is.

**`calc()` done** (node model, type classification, `+ - * /`, `min`/`max`/
`clamp`, and the expression parser). 550 checks green.

The subtle rule here is CSS Values 4 §10.1: **`+` and `-` must be surrounded by
whitespace**, because the tokenizer folds a leading sign into the number. So
`calc(1px+2px)` arrives as `Dimension("1px")` then `Dimension("+2px")`, and
`calc(1px -2px)` as two dimensions separated by whitespace — both are errors,
not additions. `*` and `/` have no such rule. All four cases are pinned.

Also worth noting: `clamp(MIN, VAL, MAX)` is `max(MIN, min(VAL, MAX))`, which is
**not symmetric** when `MIN > MAX` — the spec makes MIN win, and the operand
order reproduces that.

Unsupported math functions (`round`, `mod`, `rem`, `pow`, `sqrt`, `log`, `exp`,
`sign`, `hypot`, trig) are **rejected at parse time** rather than kept as opaque
values. Silently mis-evaluating `round()` or `sin()` would be far worse than
refusing the declaration, and the oracle would not catch it — both engines would
simply be wrong in different ways.

Still deferred: `var()` inside calc (needs the cascade), relative-colour channel
idents (need the colour parser), and `color-mix()` / `oklab()`.

**Tally: 13 wrong test expectations, 2 port bugs** (both in the selector parser, both found by test), plus two stale assertions of my own making: tests written when `rgb()` and `calc()` still round-tripped as generic function calls, which started downcasting to the wrong type once those functions began evaluating. UBSan caught both as bad downcasts rather than letting them read garbage. One of them segfaulted the
suite — a `CHECK` on `declarations.size() == 1` recorded a failure without
short-circuiting, and the next line indexed the empty vector. The test helpers
are now bounds-checked and return null rather than indexing off the end.

## Phase 3 — Cascade and selectors (~12k LOC)

`Runtime/Css/Cascade` (12,340) plus selectors, media and container queries. Port
the version-keyed invalidation contract *as designed* — do not simplify it and
plan to add it back.

**Exit:** computed-style dumps diff clean across the corpus. Incremental
invalidation benchmarked: a `:hover` flip must not re-cascade the document.
(C# reference: 0.08 ms vs 8.3 ms full.)

**Selector model and parser done** (`SimpleSelector`, `CompoundSelector`,
`Specificity`, `NthExpression`, `ElementState`, `SelectorParser`). 654 checks
green; **all 131 selectors in the demo stylesheet parse.** `SelectorMatcher.cs`
(1,069 LOC) is next.

Note the C# selector parser works on **raw characters, not CSS tokens** — it is
handed text already sliced out of the rule prelude. Ported the same way rather
than routed through `CssTokenizer`, so escape and whitespace handling stay
identical.

**First two real port bugs of the phase**, both caught by tests rather than by
reading:

1. **`parse_sequence` was missing its leading `skip_ws()`.** The C#
   `ParseSequence` opens with `SkipWhitespace()`; I dropped it, so any selector
   with leading whitespace failed outright. Rule preludes are trimmed before
   they reach here, which is exactly why this would have survived a long time
   before surfacing somewhere awkward.
2. **`:has()` takes a *relative* selector list, not an ordinary one.** Its items
   may lead with a combinator (`:has(> .child)`), which a normal sequence
   rejects. The C# encodes the leading relation by prepending a synthetic
   universal "anchor" compound, which the matcher then strips — so the anchor is
   load-bearing for the matcher, not cosmetic. Also ported the guard that
   `:has()` may not nest inside `:has()`, since the matcher has no base case for
   a self-referential subject.

Specificity carries the rules the cascade depends on: `:where()` contributes
zero, `:is()`/`:not()`/`:has()` take the max of their list, and
`:nth-child(An+B of S)` is `(0,1,0)` **plus** the max of `S`.

**Matcher done** (`SelectorMatcher.cs`, 1,069 LOC). 726 checks green — first
run, no failures. This is the first code that joins the CSS side to the DOM
from Phase 1.

**End-to-end on the real demo:** all 131 selectors parse and match against the
real 358-element document, producing **864 total matches**. 118 selectors match
at least one element; the other 13 are all accounted for — 7 are `::before` /
`::after` (a selector ending in a pseudo-element correctly never matches an
element; the cascade routes those separately), 5 are `:hover` / `:active` /
`:checked` under a null state provider, and `.col` is genuinely unused in the
demo HTML. Zero unexplained.

Two things the C# is careful about and the port keeps:

* **Attribute comparisons are code-point (ordinal), not culture-sensitive.**
  C# pins `StringComparison.Ordinal` precisely because the default overloads
  fold Turkish dotted/dotless i and German ß↔SS per locale — a selector would
  otherwise behave differently depending on the machine's locale.
* **`:has()` walks forward only.** Per §17.4 the inner traversal is anchored at
  the subject and moves outward (down for descendant, right for siblings); it
  must never walk up through parents, which would escape the relative scope. It
  therefore cannot delegate to the main right-to-left `match_sequence`, and has
  its own forward chain walker. `#l1:has(ul)` must NOT match when the `<ul>` is
  an ancestor — that is a test.

Deferred and reported as **non-matching rather than guessed**: the form-state
pseudo-classes (`:valid`, `:invalid`, `:in-range`, `:required`, `:read-only`,
`:default`, …) and `:popover-open` / `:modal`, all of which need the Forms
layer. False is the honest answer; true would silently apply styles that should
not apply.

**Property registry and ComputedStyle done** — the storage the whole cascade
writes into. 792 checks green.

The registry carries **334 properties, 68 of them inherited**, both counts
cross-checked against the C# source rather than eyeballed. Extracted
mechanically like the colour table, and for a sharper reason: **registration
order is load-bearing.** Ids are assigned sequentially and hot paths cache them
at startup, so reordering the table silently repoints every cached id — a bug
with no error message and no obvious symptom. The generated file says so at the
top.

Re-registration deliberately keeps the existing id. `@property` can redefine a
registered custom property's initial value while the document is live; if that
reassigned the id, every cached id would repoint mid-frame. Pinned by test.

`ComputedStyle` keeps the C#'s dual occupancy representation — a `bool` vector
for single-load hot readers and a parallel 64-bit bitset so the inherit step can
walk `(parent & ~child & inheritedMask)` in O(words) instead of scanning all 334
ids. That is not redundancy, it is two different access patterns.

**The no-op-write rule is the hinge the performance story hangs on.** Setting a
property to the value it already holds must not bump the version, because the
whole invalidation architecture keys caches on version numbers — a spurious bump
re-cascades everything downstream. That is the difference between the 0.08 ms
`:hover` flip and the 8.3 ms full pass. Tested for both the indexed path and the
custom-property path.

Not ported: the per-slot parsed-`CssValue` cache. It is a memo rather than
semantics, and it interacts with the arena lifetime rules — revisit once the
cascade runs and there is something real to measure.

**Cascade resolution done** — match collection, the CSS Cascade 5 §6.4.1
ordering, inline styles, and inheritance. 855 checks green. **The full pipeline
now runs end to end on the demo: HTML → DOM → CSS → matched rules → computed
styles for all 358 elements.**

The comparator is the delicate part, and its layer axis is asymmetric in two
ways that are easy to lose in translation:

* **Normal:** a *later* layer wins, and unlayered beats every layered rule.
  Inline bypasses the layer axis entirely.
* **`!important`:** *reversed* — an *earlier* layer wins, and unlayered
  (including inline `!important`) **loses** to any layered `!important`. So the
  layer comparison must run **even when one side is inline**, unlike the normal
  case. Both directions are pinned by test.

Origin ordering flips the same way: UA < User < Author normally, Author < User <
UA for `!important`.

### Performance: the match cache landed, and it was the wrong suspect

The shape-keyed match cache is ported and works — on a 1,004-element document
with repeating shapes it takes **998 of 1,004 lookups from cache**. Total time
moved from 296 ms to 277 ms.

**Matching was never the bottleneck.** Isolating the one remaining per-element
step gives the answer:

```
initial-value fill alone: 1004 elements x 334 properties = 342 ms
```

That is the entire runtime. `compute()` writes all 334 registered initial values
into a fresh `ComputedStyle` for every element — ~335,000 `std::string`
assignments per pass — and every other cost is noise beside it.

Two consequences worth stating plainly:

1. **My earlier diagnosis was wrong.** I attributed the 136 ms to the missing
   match cache. It wasn't; the cache was a real gap but not this one. The number
   that mattered was never measured until now.
2. **The fix is the arena, not a cleverer cache.** The C# avoids this with a
   pooled `ComputedStyle` plus a bitset-driven inherit walk
   (`parent & ~child & inheritedMask`) instead of iterating all 334 ids. In C++
   the arena makes it cheaper still — the initial-value table is immutable and
   shared, so an unset slot can point at it rather than copying a string per
   element per property.

### Acting on it: lazy inheritance

`ComputedStyle` now resolves inheritance and initial values **on read** instead
of materialising them. An unset slot defers to the parent (for inherited
properties) and then to the registry's shared initial value. Nothing is copied,
and `compute()` writes only what actually cascades.

| | before | after |
|---|---:|---:|
| 1004 elements, cold cache | 296 ms | **50 ms** |
| 1004 elements, warm cache | 277 ms | **24 ms** |
| demo, 358 elements | 118 ms | **35 ms** |

**~11× on the warm path**, and correctness is unchanged: the demo still
resolves `body { color: #e8ecf2 }` through `:root`, with 0 unresolved `var()`.

Note the second row is what the shape cache was always *for* — with the eager
fill dominating, its 998 hits were invisible. Fixing the real bottleneck is what
made the cache's contribution measurable (50 ms → 24 ms).

The contract change is deliberate and tested: `get()` resolves through
inherit-then-initial, so an unset slot yields the registered initial rather than
an empty string; `contains()` remains the way to ask whether **this** style set
a property directly. A declaration dropped as invalid-at-computed-value-time is
now `unset()` rather than stamped empty, or the lazy read would return that
empty string instead of falling through.

Lifetime: the parent style must outlive the child. In a tree walk the parent's
frame sits above the child's, which satisfies it naturally — a style that
outlives its walk must not keep the pointer. Recorded in the header.

**Still no full performance claim.** The 0.08 ms `:hover` figure belongs to
incremental invalidation, which is not done — this is a cold-pass number.

**`env()` and `attr()` done.** 1098 checks green. Both run *before* `var()`, so a
custom property whose value is `attr(data-x)` or `env(safe-area-inset-top)` is
already substituted by the time a `var()` reference reads it.

The three resolvers deliberately do **not** share failure semantics, and the
difference is load-bearing:

| | unresolvable, no fallback |
|---|---|
| `var()` | invalid at computed-value time → declaration dropped |
| `env()` | same — taints the declaration |
| `attr()` | **falls back to the empty string** |

`attr()`'s leniency is the older CSS 2.1 behaviour the C# implements, not the
Values L5 rules. Reproduced rather than "fixed", and tested explicitly so the
asymmetry is visible rather than looking like an oversight.

`safe-area-inset-{top,right,bottom,left}` are pre-seeded to `0px`, so
`padding-top: env(safe-area-inset-top)` works on a host with no notch instead
of being unresolvable and dropping the declaration.

Two bugs of mine, both caught by test:

* **Nested `env()` fallbacks kept the separator's whitespace** —
  `env(a, env(b))` yielded `" 44px"`. The C# trims at the same point.
* **`%g` at low precision emits scientific notation.** My shortest-round-trip
  search tried increasing precision and accepted the first form that
  round-tripped — but `%.1g` turns `10` into `"1e+01"`, which *does* round-trip.
  So `attr(data-len length)` produced `1e+01px`. Replaced with `std::to_chars`,
  which gives the shortest round-trip form and only uses an exponent when that
  is genuinely shorter. A plausible-looking shortcut that silently corrupts
  every attr-derived dimension.

**Logical properties done** (`CascadeEngine.Logical.cs`). 1171 checks green
across gcc 13, clang 18 and ASan+UBSan+LSan. `margin-inline-start` is not a
property layout ever sees: the cascade maps it onto whichever of
`margin-left/right/top/bottom` the element's `direction` and `writing-mode` put
at the inline start, so everything downstream deals only in physical sides.

Three things here are easy to get wrong, and all three are tested:

* **A logical alias is not a fixed loser to the physical property.** The obvious
  reading — "if `margin-left` is set, leave it alone" — is wrong. The C#
  synthesises a declaration carrying the *logical* winner's
  origin/layer/specificity/source order and runs the ordinary cascade
  comparison, so `{ margin-left: 1px; margin-inline-start: 2px }` computes to
  `2px` and the reverse order computes to `1px`. I wrote the wrong version
  first; it passes every test that only ever declares one of the two.
* **Vertical writing modes rotate *both* axes.** `inline-size` becomes a
  *height*, and the block axis runs left/right — so `border-start-end-radius`
  under `vertical-rl` is the **bottom-right** corner. The physical corner name
  cannot be assembled by concatenating the two side names in argument order.
* **`sideways-lr` flips the inline direction relative to `vertical-lr`** even
  though the two share a block axis, because the glyphs rotate the other way.

The mapping runs **before** `var()`/`env()`/`attr()`, matching the C#: the alias
copies raw declaration text, so `margin-inline-start: var(--gap)` becomes
`margin-left: var(--gap)` and is substituted once, as the property it will be
laid out as. A logical declaration that is invalid at computed-value time
therefore drops the physical slot too.

Not applied to pseudo-elements — the C# aliases only on the element path.
Diverging would silently move a `::before` margin to the other side.

### Three optimisations, and the reason not to trust their numbers

Two costs were found and removed while porting the mapping, and both are real
code improvements regardless of what the clock said:

1. **The per-property winner table.** The alias needs the physical property's
   cascade key to compare against, and the first version built 334
   `MatchedDeclaration`s per element to hold them — each with a `std::string`
   for the selector text. Replaced with a generation-stamped table of trivially
   copyable keys, reused across elements with no clearing between them.
2. **The mapping itself.** ~60 string concatenations
   (`"border-" + side + "-" + component`) and ~120 registry name lookups, per
   element. The C# has pre-interned name tables for exactly this, with a
   comment calling it the second-largest source of GC pressure during animated
   repaint — I ported the code without porting the reason it exists. The
   mapping depends only on the resolved axes, of which ten are reachable, so
   each is built once as property-id pairs.
3. **A guard for the common case.** Even so, every element paid two
   inherit-chain walks for `direction`/`writing-mode`, two string allocations
   and ~60 slot probes whether or not it declared a logical property. Every
   logical property id is known, so a static bitmask ANDed against the style's
   occupied bits answers "any here?" in six words.

**The numbers that motivated all three were measured against a Debug build**,
which I discovered two commits later — the benchmark linked `build/`, and
`build/` is `CMAKE_BUILD_TYPE=Debug`. So were the earlier figures in this
document: the lazy-inheritance "296 → 50 ms cold, 277 → 24 ms warm", the
"35% cascade regression" this section originally claimed, and every "ms" quoted
in Phase 3 before this point. Treat all of them as **Debug-relative**, useful
only for comparing two Debug builds of the same code, and not comparable to the
C# figures at all.

Re-measured under `-O3`, minimum of 36 passes over the demo's 358 elements:
**3.91 ms** before logical properties, **4.16 ms** after logical properties,
`@property` and CSS-wide keywords together — about **6%** for three cascade
features. `build-release/` now exists so this is not repeated.

Both mistakes have the same shape as the earlier match-cache one: I named a
plausible culprit instead of measuring, and the plausible culprit was a real but
minor cost sitting next to the dominant one.

### The demo's stylesheet opts out of the cache entirely

Worth knowing before anyone benchmarks against it: `randhtml.css` contains three
general-sibling selectors (`#t-inv:checked ~ #panel-inventory` and two siblings
of it). Sibling composition cannot be represented in a per-element key, so the
C# disables sharing **sheet-wide** for such a stylesheet — and so does this
port. All 358 elements report `skipped`.

That means the C#'s 8.3 ms/1001-element figure cannot have come from this demo
either; it comes from PerfBench's own scenes. **Comparing the two engines on
`randhtml` would compare two uncached paths.**

**`@property` done** (`AtPropertyRegistry.cs`). 1253 checks green across gcc 13,
clang 18 and ASan+UBSan+LSan. A stylesheet can now declare a typed custom
property, and the cascade honours all three descriptors: `inherits: false`,
the typed `initial-value`, and syntax validation of authored values.

`inherits: false` is the interesting one, because this port resolves custom
properties **lazily** — `ComputedStyle::get` on an unset `--name` walks to the
parent — where the C# materialises the parent's customs into every child. There
is no registry pointer to consult mid-walk. It turns out not to need one: the
C# also stamps each descriptor's initial value onto every element where the
property is unset, so seeding a non-inheriting property locally means the lazy
walk never reaches an ancestor. Same observable result, no plumbing.

Three details worth keeping:

* **A missing descriptor is not an empty one.** `initial-value: ;` is valid
  under `syntax: "*"`; a missing `initial-value` invalidates the rule. Modelled
  as `std::optional<std::string>` rather than the empty string.
* **`unset` on a non-inheriting property is `initial`, not `inherit`.** Every
  custom property looks inherited to a keyword resolver, so only the registry
  can tell these apart — the C# calls this out as ATPROP-1 and intercepts
  before its resolver runs. Ported at the same point.
* **The syntax validators reproduce the reference's looseness.** `<color>`
  checks only the *length* of a hex value, so `#zzzz` validates. Tightening it
  would reject values the reference engine accepts, which is the wrong
  direction for a differential port; the test says so explicitly.

Structurally, validation moved: the C# validates inside `CssParser` and never
constructs the rule object, while this port lets the generic at-rule survive
parsing and validates during rule compilation, so the parser stays free of
cascade-layer types. An `@property` rule has no other effect on a sheet, so the
two are equivalent from the cascade's side.

### A latent number-parsing bug, surfaced by a validator test

`css_parse_double` checked `from_chars` for an error but never that it had
consumed the whole string, so `"10m"` parsed as `10`. C#'s `double.TryParse`
requires the entire string to be the number.

It had been harmless: the only caller was the CSS tokenizer, which hands it an
exact numeric slice it just scanned. The `@property` `<length>` validator is the
first caller to pass arbitrary author text, and `10min` — which ends with the
unit `in` — validated as a length because its `10m` prefix "parsed". Fixed at
the source rather than in the validator, since every future caller would
inherit the same trap.

**CSS-wide keywords done** (`KeywordResolver.cs`). 1315 checks green across gcc
13, clang 18 and ASan+UBSan+LSan. `inherit`, `initial`, `unset`, `revert` and
`revert-layer` now resolve for registered properties and for custom properties.

Resolution runs **last**, after var()/env()/attr(), which is what makes
`inherit` mean the parent's *substituted* computed value rather than the literal
text `var(--x)`. Lazy inheritance simplifies it: the C# reads `inherit` off a
materialised parent style, and here `parent->get(id)` walks the parent's own
chain to the same answer.

`revert` and `revert-layer` are not keywords the resolver can answer on its own
— they need the element's full match list. So a rollback pass runs first and
substitutes the value text of the appropriate lower-priority match, and only
when there is no such match does the keyword survive to the resolver, which
collapses it to `initial`. Rolled-back text is resolved in turn, so a UA rule
saying `inherit`, reached through an author `revert`, still inherits; the chain
is capped at four hops.

Two axis details that are easy to invert:

* **`revert` drops the whole ORIGIN, `revert-layer` drops one LAYER.** The
  former scans for the highest-priority match at any origin strictly below the
  winner's; the latter finds the *nearest* lower layer at the same origin, then
  the latest match within that layer, and falls through to `revert` when the
  origin has no lower layer.
* **Custom properties have an empty initial value, even when registered.** The
  C# builds a synthesised descriptor that knows nothing of the `@property`
  registry, so `--sz: initial` resolves to `""`, which then fails the typed
  syntax check and lands on the descriptor's initial-value. Under `syntax: "*"`
  the empty string is valid and really does clear the property. Ported by the
  same two-step route, because a shortcut straight to the descriptor's value
  would diverge for the universal syntax.

`@layer` is parsed but not yet compiled into ordinals, so no stylesheet can
currently produce a layered match and `revert-layer` always degrades to
`revert` end-to-end. The two-pass layer logic is unit-tested directly against
synthetic match lists rather than left unverified until `@layer` lands.

### A cache-staleness trap, found by a test that looked like it was wrong

`add_stylesheet` did not invalidate the shape-keyed match cache. A sheet added
after the first `compute()` therefore applied only to elements that happened to
miss the cache — which reads as a selector bug, not a staleness bug. It cost
some minutes of suspecting the keyword resolver before the pattern (only the
*second* sheet's rules missing, only on cached elements) gave it away.

### Known-incomplete, called out rather than left implicit

* ~~Conditional at-rules are not evaluated~~ — **`@media` and `@supports` now
  gate their bodies.** `@container` still applies unconditionally, because it
  needs per-element container sizes that only the layout engine can supply;
  applying is the less-wrong default for a UI toolkit, and it is recorded here
  rather than left to be discovered.
* ~~`var()` is unresolved~~ — **done**. `env()` and `attr()` **also done**.
* ~~`@property`'s `inherits: false` is not honoured~~ — **done**, along with
  typed initial values and syntax validation.
* ~~CSS-wide keywords are not resolved~~ — **done** for registered and custom
  properties alike, including `revert`/`revert-layer` rollback.
* **`@layer` is parsed but not compiled into layer ordinals.** Every rule is
  unlayered, so the cascade's layer axis and `revert-layer` are exercised only
  by unit tests, not by any stylesheet.

**`var()` resolution done** (`VariableResolver.cs`). 884 checks green. On the
demo, `body { color: var(--ink) }` now resolves to `#e8ecf2` through `:root`'s
inheritance, and **0 of 119,572 computed values still contain an unresolved
`var()`.**

The rule that shapes the API: CSS Custom Properties L1 §3 says a var() that
cannot resolve and has no usable fallback makes the **entire declaration**
invalid at computed-value time. So the resolver returns a bool rather than a
best-effort string — substituting an empty string would leave a syntactically
broken declaration in place, and the cascade must instead *drop* it so the
property falls back to its inherited or initial value. The C# encodes this with
a reference-equal sentinel string; a bool cannot leak to a caller as a real
value.

§3.1's cycle handling has one genuinely subtle rule, and it is tested: **a
fallback must not rescue a cycle member.** Given `--a: var(--b)` and
`--b: var(--a, safe)`, the `safe` fallback would otherwise resolve through the
still-open stack frame and paper over the cycle. Once a name is known to be in a
cycle, every later reference to it is invalid regardless of its own fallback.

**`@media` / `@supports` evaluation done.** 945 checks green. Conditions are
evaluated at rule-compile time, so a false condition contributes **no rules at
all** rather than rules that silently apply.

This was not academic. The demo stylesheet carries four `@media` blocks —
`(min-aspect-ratio: 19/9)`, `(max-aspect-ratio: 4/3)`, `(orientation: portrait)`
and `(orientation: landscape)` — i.e. two **mutually contradictory pairs**.
Before gating, all four applied simultaneously at every viewport. Now three
different viewports produce three different rule sets:

| viewport | matched declarations across all `<div>` |
|---|---:|
| landscape 1920×1080 | 2,026 |
| portrait 800×1200 | 2,042 |
| ultrawide 2560×1080 | 2,027 |

The design rule throughout: **an unknown feature evaluates to false.** An
unrecognised condition must hide its block rather than apply unconditionally —
the opposite default would make every future CSS feature a silent styling bug.
Same for `@supports`: a malformed condition is unsupported, not vacuously true.

**Pseudo-element cascade done** (`CascadeEngine.PseudoElements.cs`). 996 checks
green. On the demo, the seven `::before`/`::after` rules that previously
produced nothing now generate **4 `::before` and 1 `::after` box, 3 with
non-empty text.**

Three semantics worth recording, each a place a port can be quietly wrong:

* **"No matching rule" means no box, not an empty box.** `compute_pseudo_element`
  returns `false` rather than an empty `ComputedStyle`, so the box builder can
  distinguish "the author styled nothing here" from "the author styled an empty
  thing". The C# returns null for the same reason.
* **A pseudo inherits from its ORIGINATING element, not the host's parent.**
  `#outer { color: blue } #a { color: green }` gives `#a::before` green.
  Non-inherited properties still fall to their initial, so a host `width: 100px`
  does not leak into the pseudo.
* **`content: none`/`normal` suppress the box, but `content: ""` does not** —
  an empty string still generates a box. `attr()`, `counter()` and `url()`
  report "no box" rather than rendering their literal function text, matching
  the C#'s v1 scope.

Pseudo rules live in their own buckets keyed by pseudo name, so they are never
scanned and rejected once per element — and a test pins that they cannot leak
into ordinary element matching.

**Shape-keyed match cache done.** 1045 checks green. The key folds tag, id,
classes (commutative XOR, so token order cannot shift it), attribute names *and*
values, the full ancestor chain including ancestor state bits, and — when the
sheet uses index-positional pseudos — sibling index and count.

The opt-outs are where the correctness lives, because a wrong cache does not
fail loudly, it silently serves one element's styles to another. Ported
faithfully, each with a test:

* **`style=""`** — inline declarations are invisible to a tag/class/attribute key.
* **Sibling combinators and of-type pseudos** — the match depends on which tags
  *precede* the element, which no per-element key can represent. Disables
  sharing sheet-wide.
* **`:has()`** — depends on descendant content the key cannot see, and a
  descendant mutation cannot invalidate an ancestor entry (the key is a hash
  with no reverse index).
* **Index-positional pseudos** fold sibling index/count into the key rather than
  opting out. The C# comment records what happens otherwise: `li:nth-child(odd)`
  serves row 1's match set to every identical sibling and **zebra striping
  paints every row**. That exact case is now a regression test.

## Phase 4 — Block and inline layout + software paint (~15k LOC)

Layout root files (11,775) minus the specialised modes, `Layout/Boxes` (590),
plus `Runtime/Paint` command types and `BoxToPaintConverter` (4,302) at the
altitude decided in ARCHITECTURE.md — the core now tessellates, so this is
larger than the C# original by design.

Port `SoftwareRasterizer` here as the first backend.

**Exit:** `corpus/block/` and `corpus/inline/` diff clean; the 38 golden PNGs
match. The software backend should land in the low hundreds of lines, not 1,592
— if it doesn't, the render interface was not lowered enough.

> **The size half of that exit test is met: 273 lines including its header, and
> it draws gradients.** See "The software backend" below. The corpus and golden
> comparisons still wait on the oracle and on text rendering.

**Box tree done** (`Layout/Boxes`, 590 LOC). 1382 checks green across gcc 13,
clang 18 and ASan+UBSan+LSan. This is the data model everything in Phases 4-8
writes into, so its shape is worth stating precisely — it departs from the C# in
three ways, all of them required by CONVENTIONS.md rather than chosen.

**One struct with a kind tag, not a class hierarchy.** The C# has
`Box -> BlockBox -> AnonymousBlockBox` plus three sibling subclasses. RTTI is off
here, the tree is walked constantly, and arena storage needs a uniform element
size — a tag is the only option that satisfies all three.

**Stable indices, not pointers.** Boxes live in one contiguous vector reset (not
freed) between passes, so any pointer into it is invalidated by the next
allocation. `BoxId` survives. This is the structural difference most able to
corrupt the tree silently, so there is a test that grows the arena to 5,000
boxes and re-checks an id taken before the growth.

**An intrusive sibling chain, not a `List<Box>` per box.** The C# allocates one
list per box per frame; the zero-allocation-per-frame gate does not allow it.
Append, insert-first, remove and replace are all O(1) on the chain.

Every structural test walks the children forwards AND backwards. A half-updated
doubly-linked list still iterates correctly in one direction, and the failure
surfaces much later, somewhere else. `replace_child` is the case that needs it:
the replacement may already be attached elsewhere, and without unlinking it
first both parents keep a link through it.

**Deferred fields, listed rather than silently dropped.** `BlockBox` carries
shrink-to-fit caches, `GridStretched*` / `FlexCrossStretched*` flags and a
grid-area containing block; those belong to Phases 6 and 7 and are not modelled
yet. Neither is the incremental-layout machinery (`CachedDigest`, `Version`,
`ReuseContent`, `PoolGeneration`, `InFreeList`, `ResetForPool`, the `Recycled`
callback) — most of which is pool bookkeeping that the arena replaces outright,
the rest belonging to `LayoutEngine.Incremental.cs`. The paint-side caches
(`PaintCache`, `WrapperCache`, `SubtreeHas*`) are decided by ARCHITECTURE.md's
lower render interface and are not a mechanical port.

**Box builder done** (`BoxBuilder.cs` core + `BoxFinalize.cs`). 1460 checks
green across gcc 13, clang 18, ASan+UBSan+LSan and Release. DOM plus computed
style in, box tree out.

`display` becomes a **field**, not a subclass. The C# encodes it as a tower —
`FlexBox`, `GridBox`, `TableBox`, `MulticolBox`, `TableRowGroupBox` — which the
single-struct box cannot have. A `DisplayKind` field carries the same
information and lets a box change formatting context without being reallocated.
An unrecognised value computes to `inline`, the initial value, so an author typo
degrades like an omitted declaration rather than deleting content.

**Blockification is three separate rules that all produce a block box**, and
conflating them would be easy:

* CSS 2.1 §9.7 — a floated or out-of-flow element with an inline outer display
  becomes block-level. Authors write `<span style="float:left">` and expect
  block-flow semantics; without this the box is an inline box and block layout
  never sees it as a float.
* CSS Flexbox §4 / Grid §6 — every in-flow child of a flex or grid container is
  blockified, `inline-block` → `block`, `inline-flex` → `flex`, and so on.
* The §9.7 rule is **skipped** inside a flex or grid container, because flex and
  grid items cannot float (Flexbox §3, Grid §6.4). The child is blockified
  anyway as an item, so the box kind is the same — but for a different reason,
  and the port keeps the distinction rather than merging the branches.

The reason the second rule matters is in the C# as a comment about a real bug:
without it the anonymous-block pass sees the `is_inline_block` flag and sweeps a
whole row of flex items into ONE anonymous wrapper, so per-item sizing is never
applied to any of them.

### The anonymous-block pass, and the whitespace rule that carries it

CSS 2.1 §9.2.1.1: a block container holds either only inline-level boxes or only
block-level ones. Where an author mixes them, each RUN of consecutive inline
children is wrapped in one anonymous block — not one wrapper per child — so
every layout pass below may assume one case or the other.

The load-bearing detail is that **a run consisting entirely of whitespace text
generates no box at all.** The newlines between block siblings in formatted HTML
are real text nodes (verified: `<div>\n  <div/>\n  <div/>\n</div>` gives the
outer div five children, three of them whitespace text). Without the rule, every
pair of block siblings in every hand-written document would be separated by an
empty anonymous block.

Two classification details worth stating because they are not obvious from the
spec text:

* An **anonymous block is invisible to the classification** — it is a product of
  this pass, never an input to it.
* An **inline-block counts as inline** here, so it joins the anonymous wrapper
  beside a text run rather than standing alone as a block sibling.

Raw text directly inside a flex or grid container gets the same anonymous
wrapper, for a different reason: element children were blockified on the way in,
text bypasses that branch, and an unwrapped text child leaves the container with
zero items and collapsed to its padding.

**Deferred, listed rather than dropped:** `::before`/`::after`/`::marker`
injection and list-item markers (they need the counter context, which is the
other unported half of `CascadeEngine`), `::backdrop` synthesis, `<img>`
intrinsic sizing and `field-sizing: content` (both need registries this port has
no equivalent of yet), the anonymous-table insertion pass, and the
body → html background propagation, which is paint's concern.

**Style resolver done** (the length and font half of `StyleResolver.cs`).
1545 checks green across gcc 13, clang 18, ASan+UBSan+LSan and Release. This is
the layer between the cascade's strings and layout's pixels: `LayoutContext`,
`ResolvedLength`, font-size and line-height resolution, border widths, and the
box-shorthand split.

**`ResolvedLength` is five kinds, and collapsing any two would be a bug.**
`auto` and `none` are distinct and neither is zero. A percentage with no basis
surfaces as `Percent` rather than resolving against zero, so the caller keeps
its own fallback. `fit-content(<length>)` surfaces the resolved argument for the
caller to clamp against min- and max-content, while the bare `fit-content`
keyword degrades to `auto` until intrinsic sizing exists. A value that fails to
parse is `auto`, not zero.

**The same syntax means different things in the two font properties.** A
unitless `font-size: 20` is read as pixels; a unitless `line-height: 1.5` is a
multiplier. Both are ported as the reference has them and both are pinned by
test, because the pair is exactly the kind of thing a port silently unifies.

**A border-width that fails to parse is 0, not the initial `medium`.** A border
that could not be understood should not appear.

**The box shorthand is a fallback, not an override** — in this function. I
recorded here that the resulting behaviour matched the reference. **It does
not**, and the correction is in the block-layout section below: the C# cascade
expands shorthands, so `BoxSides`'s fallback is nearly dead code there. The
split itself is paren-depth aware, so `padding: calc(1px + 2px) 8px` stays two
tokens.

### An `em` chain that stops compounding after two levels

`FontSizePx(style, parentStyle, ctx)` resolves the parent's own font-size with a
**null grandparent** — that is, against the root. Every call site passes
`box.Parent?.Style`, so the shape is deliberate and not an artefact of one
caller.

The consequence: with `#a{font-size:2em} #b{font-size:2em} #c{font-size:2em}`
nested, `a` is 32px and `b` is 64px, both correct. But `c` resolves its `2em`
against `b` re-derived as 32px rather than its real 64px, giving **64px where a
browser gives 128px**. The port reproduces this exactly and pins it by test —
differential parity is the goal, and "fixing" it here would guarantee a
divergence from the reference on every three-deep `em` chain.

Flagged for the oracle rather than acted on: this is read off the C# source, and
whether it reaches the same numbers end-to-end depends on the cascade storing
specified rather than computed values for inherited properties, which it does.
It is the first candidate divergence found in Phase 4 and the first one where
the right answer might be to change *both* engines.

**Box model done** (the `ApplyBoxModel` half of `BlockLayout.cs`). 1602 checks
green across gcc 13, clang 18, ASan+UBSan+LSan and Release. Padding, border and
margin edges, the used width, and a definite height where one exists — the first
slice that produces real geometry.

The details that carry it:

* **`box.width` and `box.height` are always BORDER-box**, because paint and hit
  testing treat them as the outer rect. Under the default `content-box` sizing
  the frame is added before stamping, and min-/max-width share width's basis, so
  a content-box bound also needs the frame added before it is compared.
* **Percentage padding and margin resolve against the containing block's WIDTH
  on every edge** — `margin-top: 10%` of a 400px-wide block is 40px, not a
  fraction of its height.
* **`min` beats `max` when they conflict** (CSS Sizing L3 §5.2), which falls out
  of applying max first and min second rather than needing a special case.
* **A border edge whose style is `none` or `hidden` is zero wide** whatever
  border-width says. Since `none` is the initial border-style, setting only
  border-width gives no border — correct, and a frequent surprise.
* **Auto-margin centring needs free space to distribute.** A still-filling auto
  width does not centre; an auto width that a max-width clamp shrank below its
  fill width does, which is the `width:auto; max-width:X; margin:0 auto`
  pattern. Floats, inline-blocks and out-of-flow boxes are excluded — the C#
  carries a note that without the out-of-flow exclusion, `position:absolute;
  left:50%; margin:0 auto` gets the in-flow centring margin added to its offset
  and lands far off centre.
* **`aspect-ratio` derives the width as a BORDER-box value directly**, with no
  frame added, measured against the authored (content) height. That is not what
  `box-sizing` would suggest, and it is pinned by test as the reference's
  behaviour rather than tidied.

### The gap Phase 4 exposed in Phase 3: shorthand expansion

The C# cascade expands every shorthand into its longhands — 26 expanders,
~2,600 LOC under `Css/Cascade/Shorthands/` — inside `compute()`, and **drops the
shorthand declaration**. Each emitted longhand carries the shorthand's full
cascade key (origin, specificity, source index, in-rule index, layer), so
ordinary source order settles conflicts between a shorthand and a longhand.

This port has none of it, and I did not know that until the box model needed
`border-top-width` and got nothing. Three consequences:

* `{ padding: 5px; padding-left: 20px }` gives 5/5/5/20 in the C# and in a
  browser. Here it gives **0/0/0/20**, because one non-initial longhand
  suppresses the whole `BoxSides` fallback.
* `border: solid 5px` sets **nothing**, because nothing writes
  `border-*-style`, and a zero border-style zeroes the edge.
* `StyleResolver.BoxSides`, which I ported last tick as the mechanism for
  shorthands, is in the C# only the path taken when expansion *fails*.

**The correction that matters most is to my own record.** Last tick I wrote here
that `BoxSides`'s longhand-suppresses-shorthand behaviour was "a real divergence
from a browser" — implying the reference shared it. It does not; the reference
behaves like a browser, and the divergence is mine. That entry is now corrected
in place.

`test_shorthand_expansion_gap` pins the current behaviour in the suite, with the
browser/C# answer written next to each assertion, so the gap is visible where
the work happens and not only in this document. Those assertions are meant to
fail when expansion lands.

**Shorthand expansion landed in the next commit** — see below.

**Shorthand expansion done** (the box-model half of `Css/Cascade/Shorthands/`).
1684 checks green across gcc 13, clang 18, ASan+UBSan+LSan and Release. The gap
recorded last tick is closed, and its pinned assertions were rewritten to the
values a browser gives.

**Expansion happens at rule-compile time, not per element.** The C# expands the
match list inside `compute()`, once per element per pass; the same work done
once per rule gives identical results because the expansion depends only on the
declaration's own text. Each emitted longhand still carries the shorthand's
cascade key, which is what makes source order — not a shorthand-versus-longhand
precedence rule — settle the conflict.

Three behaviours that are easy to get wrong and are pinned by test:

* **An omitted component resets to its INITIAL value**, it is not left alone.
  `{ border-width: 9px; border: solid }` gives a 3px border, because `border`
  writes `medium` into the width it did not mention.
* **A malformed shorthand is still dropped.** It emits nothing AND removes
  itself, so the affected longhands keep whatever an earlier declaration gave
  them. The declaration is invalid, not partially applied.
* **`border-radius` fills corners TL, TR, BR, BL** — not the
  top/right/bottom/left of the edge shorthands — and a `/` splits horizontal
  from vertical radii. `place-*` is align-then-justify, the reverse of the
  x-then-y order every other two-value shorthand uses.

### `padding: 1lh` produces no padding

The shorthand `<length>` validator has its own unit list, and it predates `lh`,
`cap`, `ic`, `cq*` and the `sv*`/`lv*`/`dv*` family. A token it does not
recognise fails validation, the shorthand expands to nothing, and — by the rule
above — the declaration is dropped entirely. So `padding: 1lh` yields **zero**
padding while `padding-top: 1lh` yields 40px on a 2×20px line-height.

Ported exactly, including the omissions, and pinned by two tests that say what
they are. Widening the list would change which declarations reach the longhands
at all, which is not a change to make blind — it is a second candidate
divergence for the oracle, alongside the `em` chain.

**Deferred, and listed:** `background`, `mask`, `border-image`, `font`,
`transition`, `animation` (all comma-separated multi-layer parsers), `all`, the
logical border family, `flex`, `flex-flow`, `columns`, `column-rule`,
`list-style`, `text-decoration` and `-webkit-text-stroke`. The box model needs
none of them; each is its own small parser.

**Cost:** the demo cascade goes from 4.16 ms to 4.62 ms for 358 elements
(Release, minimum of 24 passes). That is real work, not overhead — `border: 1px
solid black` is twelve declarations to stamp where it used to be one that
nothing read.

**Block flow and margin collapsing done** (`MarginCollapsing.cs` and the
in-flow half of `LayoutContent` / `FinalizeBlockSize`). 1768 checks green across
gcc 13, clang 18, ASan+UBSan+LSan and Release. Blocks stack, margins collapse,
and auto height follows content.

**The collapse chain is not a pairwise fold.** Across a chain of N adjoining
margins the result is `max(positives) + min(negatives)`, and folding pairwise
gives the wrong answer for a mixed-sign chain longer than two: `{+20, -15, +10,
-25}` folds to −15 where the spec gives −5. So the running max and min are
tracked and combined once, when the chain closes. `collapse_margins(a, b)` still
exists for the two-margin case and is tested separately, including that a NaN
input is treated as ABSENT rather than propagated — one bad `calc()` would
otherwise corrupt every block below it.

Three asymmetries pinned by test because each is easy to get backwards:

* **An explicit height blocks BOTTOM collapsing but not TOP.** Only padding,
  border or a BFC closes the top edge. The reference records having had this
  wrong once, which is a good reason to pin it.
* **A leading chain attaches to the PARENT'S margin-top** when the parent's top
  is open — the child then sits flush against the inner edge and the margin
  lives outside the parent entirely.
* **Floats, inline-blocks and out-of-flow boxes are barriers**, each for its own
  reason: a float's margins collapse with nothing, an inline-block's apply
  verbatim on both sides, and an out-of-flow box is placed without advancing the
  cursor at all.

### The second Phase 3 gap Phase 4 exposed: no UA stylesheet

The first block-flow test laid out nothing, because `html` was an **inline**
box. The initial value of `display` is `inline`, and this port had no user-agent
stylesheet — so every element in every document was inline and block layout had
no input.

`UserAgentStylesheet.cs` is 156 lines of plain CSS behind a string constant.
Ported verbatim (54 rules, parses in strict mode) and loaded at
`DeclarationOrigin::UserAgent`, which the cascade already ordered correctly.

Worth knowing that it is **not** the browser default, and the reference's own
comments say why: a Unity runtime always paints into a fixed viewport, so
`html, body` fill it with `margin: 0` and `overflow: hidden`, where a browser
gives body an 8px margin and lets it size to content. An author writing
`height: 100%` gets the viewport instead of zero. The same choice will need
re-examining for Godot, but changing it would be a divergence, so it is recorded
rather than adjusted.

### Inline content reports zero height, explicitly

A container of inline content returns before the block loop and reports zero
content height, because the inline formatting context is Phase 4's next slice.
That branch is written explicitly rather than left to fall out of the block
algorithm — and finding out why matters:

An inline-block between two blocks is wrapped in an anonymous block by the
box builder. Walking that wrapper with the block algorithm, it reached
`is_self_collapsing`, whose "any in-flow child disqualifies it" test SKIPS
inline-block children — so the wrapper looked self-collapsing and contributed
no height. The number was right by accident. The explicit branch makes the
limitation visible, and the test that pins it says it should fail loudly when
inline layout lands.

It also settles a question the reference leaves open: the block loop's
inline-block branch is unreachable, in both engines, because the
anonymous-block pass classifies an inline-block as inline and always wraps it.
It is defensive code.

**Deferred within block layout, and listed:** floats and `clear` (the whole
`FloatContext`), out-of-flow positioning (`PositioningPass`), `fit-content`
block sizing, the flex / grid / table / multicol modes, scroll-boundary content
reuse, and `contain: layout`/`paint` as BFC triggers.

**Floats and `clear` done** (`Floats/` plus the float paths in `LayoutContent`).
1839 checks green across gcc 13, clang 18, ASan+UBSan+LSan and Release. Left and
right floats stack and step down when they do not fit, `clear` pushes past them,
and a BFC grows to enclose the floats inside it.

The structural points:

* **A float context is per-BFC and coordinates are BFC-local.** Floats never
  escape the BFC that contains them — a `clear` outside an `overflow: hidden`
  box finds nothing inside it. Each entry is the float's MARGIN box, because
  that is what later floats and line boxes must not overlap.
* **The y range is half-open.** A float ending exactly at `y` no longer intrudes
  there, which is what makes a float and the box directly beneath it not
  interfere.
* **A float that never fits still gets placed.** `find_placement_y` steps down
  to each row where an intruding float ends; when none is left it returns the
  last row tried and the float overflows, which is what CSS 2.1 asks for.
* **Floats are placed in a pre-pass**, against a running estimate of the cursor
  that ignores margin collapsing, so the context is populated before a later
  sibling lays out content that must flow around them. The placement loop then
  computes exact positions. The reference accepts the same trade and says so.

### Two more candidate divergences, pinned rather than fixed

Both are places where the reference and a browser disagree and I chose the
reference, on the same principle as the `em` chain and `padding: 1lh`.

**Clearance does not absorb the margin.** With a float ending at 80 and
`{ clear: left; margin-top: 30px }`, this engine lands the box at **110**: the
clear line moves the top MARGIN edge, and the box's own margin then applies on
top. Chrome lets clearance absorb the margin and puts the border edge at 80. The
over-trigger is more visible in the second case: a 100px margin already clears a
float ending at 40, so the spec introduces no clearance at all and the box
lands at 100 — here it lands at **140**, because the clear line is tested
against the cursor BEFORE the child's own margin is folded in.

**A box whose only children are floats is self-collapsing.** It has no in-flow
content, so the flow cursor never advances past it — even though §10.6.7 has
already given it a height enclosing the float. The box after it overlaps it
entirely. The reference reaches the same answer because its `IsSelfCollapsing`
tests the STYLE height rather than the computed one. Chrome does not do this.

That makes four candidate divergences waiting on the oracle: the two-level `em`
chain, `padding: 1lh` expanding to nothing, clearance-plus-margin, and the
float-only self-collapsing box. Each is pinned by a test that names it, so none
can be silently "fixed" into a mismatch.

~~**Deferred:** shrink-to-fit for an auto-width float~~ — **done**, once inline
layout could measure. See below.

**Inline layout: first working slice** (the core of `InlineLayout.cs` /
`LineBreaker.cs`, plus `IFontMetrics` and `MonoFontMetrics`). 1910 checks green
across gcc 13, clang 18, ASan+UBSan+LSan and Release. Text now measures, wraps
and lands on line boxes with real baselines.

**The font seam is four methods**: `line_height`, `ascent`, `descent`, and a
`measure` that takes a view. That is the entire surface a Godot `TextServer` or
a FreeType+HarfBuzz backend has to implement, and Phase 5 can choose between
them without touching layout — which was the point of pinning it down now
rather than later.

`MonoFontMetrics` is the deterministic stand-in: every value is a multiple of
the font size, so results are identical on every machine. The parameterless
shape (0.5 / 1.2 / 0.8 / 0.4) is what the reference's own arithmetic is pinned
against — "5 chars x 16px = 40px" — so it stays fixed.

**It decodes UTF-8 rather than counting bytes.** An accented letter is one
glyph, not two; an emoji is one WIDE glyph at ~1.3em, not four Latin advances.
Browsers render emoji from a separate face, and charging them the Latin advance
underestimates a line by roughly 17px each. The BMP allowlist is deliberate:
neighbouring ranges (Miscellaneous Technical, most Miscellaneous Symbols, Math)
are text-presented and keep the Latin advance.

**The line box is the output shape, not a detail.** The container's children are
replaced by `Line` boxes, each holding the runs that landed on it. Three things
this pins:

* **The line's height is its tallest content and its baseline the deepest
  ascent.** A 32px span on a line of 16px text makes the line 38.4px tall and
  pushes the 16px run DOWN so both sit on one baseline — rather than each run
  sitting at the line's top edge and overlapping the line above.
* **Half-leading splits evenly.** A `line-height` larger than the text puts half
  the extra above and half below, which is what centres text in its line.
* **A trailing collapsed space is trimmed at the line end** and a leading one is
  dropped. Without the first, every centred or right-aligned line is off by a
  space width; without the second, every wrapped line is indented by one.

`text-align` records its shift on the line as well as applying it, so a later
pass — a flex item re-running its inline content once its width settles — can
undo the previous offset instead of stamping a new one on top. The reference
carries a note about exactly that bug.

**Deferred, and this is the large half:** bidi and the Unicode line-breaking
classes, CJK kinsoku, hyphenation, `overflow-wrap` / `word-break` (a word wider
than its line currently overflows rather than splitting), preserved whitespace
beyond treating a `pre` run as one unbreakable fragment, tabs, `letter-spacing`
and `word-spacing`, justification, ellipsis, `vertical-align` beyond baseline,
and inline-level atoms — an inline-block is skipped rather than placed, so the
text around it still measures correctly but the atom does not appear on the
line. That last one also still blocks float shrink-to-fit.

**Shrink-to-fit and inline atoms done.** 1950 checks green across gcc 13, clang
18, ASan+UBSan+LSan and Release. An auto-width float now hugs its content, and
an inline-block is placed on a line instead of being skipped.

**The two intrinsic widths are measured, not estimated.** Laying the content out
at a huge width makes every line as long as it can be — that is max-content.
Laying it out at width 1 forces a break at every opportunity, so the widest
resulting line is the longest unbreakable run — that is min-content. The
shrink-to-fit width is then `min(max-content, max(min-content, available))`,
clamped by min- and max-width.

That means a container is laid out **three times**, and the first pass replaces
its children with line boxes — after which the source runs cannot be walked
again. The reference snapshots and restores the child list; this port caches the
collected inline items per container instead, which is both cheaper and harder
to get wrong.

**An inline-block is an atom**: placed whole, never split, and reparented onto
its line box — so its coordinates become line-relative and the line carries the
page offset. Its baseline is its bottom margin edge, which is why a tall atom
pushes the line's baseline down and the text beside it follows.

### A real bug the atoms found: `append_child` did not unlink

Moving an atom onto its line box left it linked into its block container as
well. The container's chain then ran *through* a box living under the line box,
so clearing the container's children walked into the line box's list and removed
the run after the atom — the `y` in `x<span>ab</span>y` silently vanished.

`replace_child` already unlinked first, and its test carried a comment about
exactly this hazard; `append_child` and `insert_child_first` did not. Fixed in
all three, with a test that reparents a middle child and checks both chains from
both directions.

### A fifth candidate divergence: shrink-to-fit below min-content

CSS 2.1 §10.3.5 gives `min(preferred, max(preferred-minimum, available))`. For a
20px container holding a 40px word that is 40 — the float overflows its
container rather than squeezing below its own min-content width. The reference
adds a final `if (fitted > avail) fitted = avail`, which contradicts the formula
and clamps to 20, so the word overflows the FLOAT instead.

Pinned, not fixed. That is five now: the two-level `em` chain, `padding: 1lh`
expanding to nothing, clearance-plus-margin, the float-only self-collapsing box,
and this.

**Positioned layout done** (`ContainingBlockResolver` and the placement core of
`PositioningPass.cs`). 2005 checks green across gcc 13, clang 18,
ASan+UBSan+LSan and Release. `relative`, `absolute` and `fixed` all place, and
the pass runs after block layout because an out-of-flow box resolves against its
containing block's FINAL geometry.

**The containing block is the padding box, not the border box.** `inset: 0` on a
child of a 5px-bordered ancestor lands 5px in on each side, and a percentage
offset resolves against that inner rect.

**A static ancestor can still capture an absolute box.** Being positioned is one
trigger; a `transform`, `filter`, `perspective`, a `will-change` naming one of
those, or layout/paint containment is another. Missing the second group is how
an `inset: 0` child of `transform: scale(1)` ends up filling the viewport
instead of its parent — the reference records that exact bug. The same set
captures `position: fixed`, which otherwise ignores positioned ancestors
entirely: a transform changes how viewport coordinates map to local ones, so a
transformed ancestor is the containing block for both.

**`auto` is absent, not zero**, and the distinction decides placement. With
neither edge of an axis given the box keeps its STATIC position — where it would
have been in flow — rather than snapping to the containing block's origin. With
both given and no explicit size it stretches between them; with both given, a
definite size and both margins `auto`, the slack splits evenly and the box
centres, which is the `inset: 0; margin: auto` dialog pattern.

`position: relative` offsets from the in-flow position without disturbing the
flow, and is over-constrained by design: with both edges of an axis given, the
start edge wins in LTR and the other is ignored (§9.4.3).

### A cache written by one path and read by another

A pinned box's children were sized against the containing block's PROVISIONAL
width during block layout, so the pin has to re-lay its content or the content
keeps the wider measure and overflows. The relayout produced an EMPTY box: the
inline-item cache was populated only by the relayout path, never by the original
layout, so the second pass collected from a container whose children were
already line boxes and found nothing.

Both paths now go through one `layout_inline_content` entry point. The
collection is cached (it cannot be redone once line boxes replace the source
runs); atom sizes are re-derived every pass, because those genuinely depend on
the width.

**Deferred:** `position: sticky` (needs a scroll position), stacking contexts
and paint order — which belong to paint, not layout — anchor positioning, the
grid-area containing block, and the flex static position for an out-of-flow flex
child.

**Paint opens: the render interface and the tessellator.** 2067 checks green
across gcc 13, clang 18, ASan+UBSan+LSan and Release. This is the slice that
tests ARCHITECTURE.md §1's central bet, so it is worth being precise about what
landed.

**The seam is indexed triangles, not drawing commands.** Seven required methods
— compile, render and release geometry; load, generate and release textures;
set scissor — plus optional transform, layer and filter operations that a
backend may ignore, with the feature degrading rather than breaking. The C#
`IRenderBackend` has twelve methods at a semantic altitude, which is why its URP
backend is 9,082 lines plus 3,904 of shader and its software rasterizer is 1,592
lines that still draw glyphs as blocks.

**The core now owns the geometry.** Rounded rects are fanned from the centre —
correct for any convex outline, and a rounded rect always is — with a zero
radius falling through to a plain indexed quad, so the common case costs four
vertices. A border is ONE ring of paired outer and inner vertices rather than
four edge quads, which is what stops a mitred corner between two colours from
double-covering.

Two spec details that are easy to miss and are pinned:

* **§5.5 radius clamping scales every corner by one factor.** Overlapping radii
  shrink together, so the shape keeps its proportions; scaling only the
  offending corner would distort it.
* **The inner edge of a border curves less than the outer.** Each radius is
  reduced by the border width on its side, floored at zero — a 2px radius under
  a 10px border is square inside.

Also: the background paints out to the BORDER box, not the padding box, so a
semi-transparent border shows it through; and an unset `border-color` is
`currentColor`, which is what makes a border follow the text colour.

**Deferred, and the list matters because the exit test depends on it:**
gradients, images and `background-*` positioning, box and text shadows, text
runs (they need a glyph atlas, so they wait on Phase 5's font backend), clip
paths, filters and blend modes beyond the interface declaration, and stacking
contexts — boxes paint in tree order today, so a positive `z-index` does not yet
lift a box above a later sibling. Also deferred is the per-box paint cache; the
current code compiles and releases geometry per box per frame, which a batching
backend will want changed.

**The software backend: 273 lines, and it renders gradients.** 2224 checks green
across gcc 13, clang 18, ASan+UBSan+LSan and Release. The whole pipeline now
runs end to end — HTML and CSS in, pixels out.

This is the measurable half of ARCHITECTURE.md §1's exit test. The C#
`SoftwareRasterizer` is **1,592 lines** and still draws glyphs as blocks and
gradients as flat fills. This one is **273 lines including its header** and
draws gradients correctly, because with the interface at triangle altitude a
gradient is just per-vertex colour interpolation — it costs nothing extra.

The rasterizer's own details, each pinned:

* **Pixels are sampled at their centre.** A rect from 2 to 7 covers columns
  2..6 — five, not six.
* **A shared edge is drawn exactly once**, by the standard top-left fill rule.
  Drawn twice it is visible wherever the colour is translucent, and the core's
  meshes share edges everywhere: a quad, a fan and a ring are all built from
  them. The test fills a translucent rect and checks every pixel reads 0.5
  rather than 0.75 along the diagonal.
* **Both windings are accepted**, normalised by flipping two vertices, because
  the tessellator emits both and carrying the sign through every comparison
  would be worse.
* **A texture modulates the vertex colour** rather than replacing it, which is
  what lets one path serve both a glyph mask and a tinted image.
* **Malformed input degrades, it does not fault.** An out-of-range index skips
  its triangle, an unknown handle draws nothing, a zero-area triangle returns
  before dividing, and an unsupported image path returns the null handle so the
  draw falls back to vertex colours.
* **The framebuffer is linear and converts on output.** A mid-grey linear value
  leaves as sRGB 188, not 128 — which is the entire reason for keeping it
  linear.

**Still deferred for the backend:** image decoding (`load_texture` returns null
by design), layers, filters and blend modes, and antialiasing — coverage is
currently a hard in/out test at the pixel centre.

**Text renders.** 7004 checks green across gcc 13, clang 18, ASan+UBSan+LSan and
Release. Cascade, layout, shape, pack, rasterize — glyphs on screen.

**The `FontInterface` is exactly ARCHITECTURE.md §2's shape**, so Phase 5's
choice between FreeType+HarfBuzz and Godot's `TextServer` stays open. The C#
bound `FontEngine.TryRenderGlyphsToTexture` by REFLECTION into undocumented
Unity internals with a TextMeshPro fallback ladder; the interfaces above it were
right, the implementation underneath never was.

`StubFont` is a built-in 5x7 ASCII face — the same role `MonoFontMetrics` plays
for measurement. It is not a stand-in for a real face: no hinting, no kerning,
one glyph per code point in order. What it gives is a rendering path that can be
asserted pixel by pixel on any machine, which is what the golden tests need
before a real backend exists. Its em box matches `MonoFontMetrics` exactly, so
measurement and rendering agree — a mismatch would show as text drifting off the
line boxes laid out for it.

**The atlas packs once and uploads once.** Shelf packing with a pixel of padding
so a filtering backend cannot bleed a neighbouring glyph in; sizes quantised to
whole pixels, so a fractional font-size from a percentage does not re-pack an
identical raster; and the coverage byte written into all four channels, so white
RGB lets the vertex colour pass through and one atlas serves text of any colour.

### Two bugs the tests caught, both about blank glyphs and the baseline

**A space was getting an atlas slot.** `glyph_metrics` reported a non-zero size
for any glyph other than the missing one, so a space rasterized to an all-zero
bitmap of real dimensions and the packer dutifully stored it — one wasted
rectangle per space in the document. A blank cell now reports no bitmap at all,
which is what a real face does.

**Glyph quads dipped below the baseline.** The bitmap is a whole number of
pixels tall (13 at 16px) but the bearing was the exact ascent (12.8), so every
quad's bottom edge sat 0.2px low. The bearing is now the rounded height, and the
bottom edge rests exactly ON the baseline. A real face's bearing is per-glyph
and has no reason to match the face ascent either.

**Deferred:** a real face backend (Phase 5's decision), SDF rasterization,
kerning and ligatures, bidi, and atlas eviction — the atlas grows until full and
then refuses, which is right for a document but not for a long-running app.

**The C ABI is in.** 7105 checks green across gcc 13, clang 18, ASan+UBSan+LSan
and Release. `include/weva_c.h` is the seam a Godot GDExtension binds through,
and the one a Unity host would bind through later — which was the architecture
decision made at the very start of this work.

Its tests are written the way a host writes code: through `weva_c.h` alone, with
no libweva C++ type in sight. **If that file ever needs a core header, the seam
has leaked**, and the test file failing to compile is the signal.

The contract, and why each part is shaped as it is:

* **Handles are indices, not pointers.** The DOM is refcounted and may move; a
  stale index is detectable where a stale pointer is undefined behaviour. Every
  lookup rejects an out-of-range handle.
* **Lengths are explicit.** No entry point requires a null-terminated buffer,
  and the bytes are copied, so a host may free them on return.
* **Every entry point tolerates a null document.** A host that failed to create
  one must not take the process down with it.
* **`weva_element_text` returns the length it WOULD have written**, so a host
  sizes with one call and fills with a second — and truncates safely if it
  guesses.
* **The draw list is borrowed, not owned.** This is the one place the
  "explicit free" rule is relaxed, in exchange for a documented lifetime: the
  buffers are valid until the next update. A per-frame copy of the whole
  display list is exactly the allocation this port exists to remove, so
  handing one back would undo the point.
* **A viewport change takes effect on the NEXT update.** A host resizing
  mid-frame must not see a half-updated document.

What crosses the boundary is triangles: a collecting backend gathers the draw
list rather than rasterizing it, so the core still does all the tessellation and
the host's own renderer issues the draws. The translation is baked into the
vertices there rather than passed through, because the ABI should hand over
geometry that is ready to upload.

~~**Deferred:** host-supplied render and font backends~~ — **done**, see below.
Still deferred: event delivery, the animation tick (`dt_seconds` is accepted and
ignored), and multi-element query.

**Host backends: a Godot host can now supply its own renderer and font.** 7140
checks green across gcc 13, clang 18, ASan+UBSan+LSan and Release. Two C
function-pointer tables mirror the C++ interfaces, with `user_data` carried
through every call so a host needs no global.

**A partially filled table degrades rather than crashing.** Every null function
falls back to the built-in, which is what lets a host adopt the tables one call
at a time — the same contract the optional methods on the C++ interface already
have. Tested with a table carrying only `render_geometry`, and with an entirely
empty one.

**The vertex layout is part of the ABI.** `Vertex` and `weva_vertex` are the
same eight floats in the same order, asserted with a `static_assert`, so a host
reads the compiled buffer with no conversion pass.

### The trap this closed: two font seams that could disagree

`FontMetrics` (what layout measures with) and `FontInterface` (what paint draws
with) are separate seams, because layout runs without a rasterizer and a
rasterizer runs without layout. But a host that registers a face needs them to
AGREE — otherwise text is laid out to one face's advances and drawn with
another's, and drifts off the line boxes made for it.

Registering a font backend now also drives measurement, through a
`FontInterfaceMetrics` adapter that shapes the text and sums the advances rather
than adding up per-glyph widths — a shaper may substitute a ligature or apply
kerning, and the width layout uses has to be the width the same call will draw.

Finding that also surfaced a real gap: **`line-height: normal` was resolving to
a hard-coded 1.2 factor**, ignoring font metrics entirely. The C# routes it
through `IFontMetrics.LineHeight`. Fixed — a host's face now governs its own
line height, and the constant remains only as the no-face fallback. It was
invisible until now because the stub's metrics happen to sum to exactly 1.2.

### The Godot host actually runs — and the first thing it did was find bugs

The extension builds against `godot-cpp` and loads in **Godot 4.7.2**, with 20
end-to-end checks green in `project/render_tests.gd`. `godot-cpp` publishes no
4.7 branch; `master` bundles the 4.7 API description, and an engine can always
dump its own (`--dump-extension-api`), so version coupling is manageable rather
than a wall. An extension built against 4.3 loaded under 4.7.2 unchanged.

Two things had to be true before any of this ran, and neither was obvious:
`.godot/extension_list.cfg` must exist or the engine loads no GDExtension at all
and reports only `Could not find type "WevaDocument"`; and headless still
validates every canvas command, which is what made the first failure visible.

**`draw_polygon` was the wrong call.** It takes a polygon *outline* and
triangulates it, so feeding it the expanded triangle soup produced garbage where
it did not fail outright ("Invalid polygon data, triangulation failed"). The
right call is `canvas_item_add_triangle_array`, which takes the index buffer
directly — the shape the core already produces. The host got *simpler*: the
per-index expansion is gone entirely. Worth noting as evidence for §1: the
render interface being at triangle altitude meant the fix was to stop
translating, not to translate better.

**One render test was wrong and the engine was right.** `[data-hide]` is
(0,1,0) and `#a` is (1,0,0), so the hiding rule lost the cascade and `display`
correctly stayed `block`. Reproduced in 30 lines of C against the ABI before
touching anything — which is the only reason it was not "fixed" into a
divergence. The rule is now `#a[data-hide]`.

That test also exposed a real host gap: **attribute removal was unreachable from
GDScript.** The ABI reads a null value as "remove", GDScript cannot express one,
and an empty string still satisfies a presence selector. Added
`remove_element_attribute`, and the test now round-trips both ways rather than
only hiding.

### Two backends, one draw list: the §1 exit test, half of it now met

`tools/weva_render` rasterises a document through the software backend and
`hosts/godot/compare_render.py` renders the same document through Godot and
compares the images. Both consume the **identical** `weva_draw` list from the
same build, so any difference is a difference between the backends with cascade,
layout and tessellation held fixed.

Blocks, borders, rounded corners and text now come out **pixel-identical** —
zero differing pixels, on a document with 5 draws and 66 triangles. Only ink
coverage gates; channel error is reported, because two rasterisers are entitled
to disagree at edges and pretending otherwise makes the check noise.

It found two things immediately:

**Godot's default linear texture filtering is wrong for this atlas.** The core
emits UVs addressing texels exactly, and the atlas is shelf-packed with no
gutter — so linear sampling both softens glyphs the core drew crisply and reads
across shelf boundaries into whatever glyph was packed next door. The host now
sets `TEXTURE_FILTER_NEAREST`.

**A real core bug: the draw list named a texture that no longer existed.** The
glyph atlas uploaded lazily at each text run. A second run that added a glyph
made it dirty again, and the upload released the texture the first run's draw
still referenced. `weva_document_textures` then published only the final one, so
a host that maps ids faithfully drew the first run **untextured** — solid blocks
where the text should be. Godot looked correct only by accident, because it
binds its single atlas whenever `texture_id != 0`.

Under a host GPU backend this is worse than a cosmetic bug: `release_texture`
would free a texture a queued draw still points at.

Fixed by packing every glyph in a pre-pass, then uploading once, so all text
draws share one handle. Pinned by
`test_abi_texture_ids_are_all_published`, which was checked against the
reintroduced bug rather than assumed to catch it: it fails on `published`
without the fix.

This is the clearest argument yet for the comparison existing at all. The core's
own 7,147 checks passed throughout — the bug was in the *contract between* the
core and a host, and only a second real host consuming the same list could see
it. 7,147 checks now green across gcc 13, clang 18, ASan+UBSan+LSan and Release.

Still outstanding on the §1 exit test: the corpus of golden PNGs, which needs
the oracle. And **the oracle has still never run** — every parity claim across
Phases 2–4 remains static analysis plus my own tests. `BaselineGen` needs
`dotnet build`, and there is no .NET SDK in this container. It stays the
highest-value work outside this loop.

### Real fonts: `TextServer` behind the C font table

`GodotFontBackend` fills `weva_font_backend` over Godot's `TextServer`, so a
document is measured and shaped by the same HarfBuzz every other control in the
engine uses. It adopts the theme's fallback face as a RID rather than loading a
font file, so a document renders in the project's own font and the host ships
none. 23 checks green in the Godot project.

Two sign-and-convention traps, both silent if got wrong. Godot's glyph offset is
the quad's top-left *below* the baseline with y growing down; the core's
`bearing_y` measures *up* to that same edge. And `face_metrics` reports a zero
line gap, because `TextServer` exposes none and its own line height is
ascent + descent — inventing one would make `line-height: normal` taller here
than in any Godot control using the same face.

**The backend comparison caught the consequence immediately**: with Godot on a
real face and `weva_render` still on the stub, agreement went from 0 differing
pixels to 14.72%. Nothing was broken — the two sides were rendering different
text. `use_engine_font` now holds the font fixed for the comparison, and it is
back to pixel-identical. Worth recording because the failure mode of a
differential harness is exactly this: a real difference that means nothing,
which if waved through teaches you to wave through the next one.

The render test for this asserts more than "a font was adopted": it checks that
the two faces *measure* differently, which is what catches a backend wired into
paint but not into metrics. The first version of that check used a `display:
block` element, whose width is its containing block's whatever the font — it
passed without measuring anything. `inline-block` shrink-to-fits and actually
tests it.

And the comparator's own gate was wrong in a way worth naming: "ink" was defined
as *not white*, which on a dark document makes every pixel ink and reports
perfect agreement while measuring nothing. It now takes each image's modal pixel
as the page colour, and fails outright when the two disagree on it.

### The oracle runs. 17/47.

I had been recording the oracle as blocked on a missing .NET SDK for the whole
port. **It was not blocked; I had never tried.** `dotnet-install.sh` installs
the 8.0 SDK into the container in about a minute. Everything below came out of
the hour after that, and none of it was reachable from static analysis.

Three small things stood between the SDK and a running reference, all bit-rot
rather than design: `BaselineGen.csproj` excluded whole directories as
"Unity-bound" when only **three files** in them actually fail outside Unity —
the rest gate their Unity code behind `#if UNITY_*`, which a plain `dotnet
build` compiles out. What remained was profiler instrumentation threaded
through the layout path, covered by a no-op shim (`HeadlessUnityShims.cs`);
anything with behaviour there would be behaviour the oracle has and Unity does
not, which is the one thing a reference must never have.

`weva_dump` is no longer the Phase 0 stub. Its walk mirrors `LayoutDump.Walk`
exactly: `html` and `body` skipped as wrappers but recursed into without
consuming a depth level, anonymous and line boxes skipped, first box per element
only. It uses `MonoFontMetrics::chrome_sans_serif()` because that is what
BaselineGen uses — the C ABI default-constructs a *different* face, and matching
the ABI instead of the oracle would have diverged every text measurement for a
reason unrelated to the engine.

`tools/oracle/run_oracle.py` runs both sides over a corpus and diffs. Tolerance
is zero, per ORACLE.md.

**First run: 15 of 47 agreed.** Not the "parity" every prior phase claimed.

#### What it found immediately: `line-height: 1` did nothing

The dominant failure was `h 16 vs 18.29` across most of the corpus — 18.29 is
16 × 1.143, the metric line height, so the port was ignoring the declared
`line-height` and falling back to `normal`. Bisecting the value showed the
shape: `1.5`, `2`, `3` and `20px` were all correct, while `1`, `1.0`, `0.5` were
not. Every failing value was one that asks for a line box SHORTER than the
font's own ascent + descent.

The cause was `std::max(content_height, max_leading)` in `flush_line`. CSS 2.1
§10.8.1 makes half-leading **signed**: a tight line-height gives a negative
half-leading and a line box shorter than its content, with the glyphs
overflowing it. Clamping made `line-height: 1` indistinguishable from `normal`.

The C# has a comment at exactly that spot recording that the negative branch was
once suppressed there too, and naming the same symptom. Two implementations, the
same mistake, found the same way.

The reference's model is also not the per-item spec model, and guessing would
have got it wrong: it lays lines out at their natural metric height, then
overrides them in a pass over the container's children **keyed on the container
declaring `line-height`**, not on the per-item values. Confirmed by asking the
oracle for a multi-line case rather than reading more code. Now matched:
`line-height: 1`, `2` and `normal` agree exactly on both height and stacking.

**17/47 after the fix.**

#### What is left, honestly

Most of the remaining 30 are features that are genuinely not ported: flex, grid,
multicol, counters, list markers, quotes, `<br>`, containment. Those are Phases
6–8 and their failures are expected.

But several are in slices this plan already called done, and they are real:

* **Floats do not shorten line boxes.** `28-floats`: text beside a float
  reports one line where the reference gives two. Floats and inline layout were
  each signed off; their *interaction* was never built.
* **`word-break` does not break.** `21-word-break`: one line against the
  reference's two.
* **A `span` lays out as a block** in `30-top-bar-and-body` — full container
  width instead of its text width.
* **An absolutely positioned box is not centred** in `31-centered-modal`
  (x 200 vs 0, y 150 vs 0).
* **Inline-block baseline alignment is off** by ~6px in `19-inline-block-row`.

None of these were visible to the 7,147 self-written checks, which is the whole
argument for the oracle and the reason ORACLE.md says to build it first. I did
not, and four phases of "parity" turned out to mean "agrees with tests I wrote
from the same misunderstanding".

### 20/47, and the number that actually matters

Three more bugs, all found by the oracle and none visible to the self-written
suite. 7,147 checks stayed green through every one of them, which is the point.

**Floats did not shorten line boxes.** CSS 2.1 §9.5: a line box beside a float
is narrowed to make room for it. `FloatContext` already had `left_extent_at` and
`right_extent_at`, and the header comment on them literally said "which is what
line-box narrowing needs" — nothing ever called them from inline layout. Floats
were signed off, inline layout was signed off, and their interaction was never
built. The narrowing has to be recomputed at the START of each line rather than
at flush, because the wrap decision compares against it.

**An inline-block's baseline was its bottom edge.** §10.8.1 gives three cases:
clipped overflow → bottom margin edge; otherwise the last line box's baseline;
otherwise the content-area bottom. Only the first was implemented, applied to
everything — a `// later slice` comment marked the gap honestly and nothing ever
came back to it. Every line holding an inline-block came out one text-descent
too tall. Also: the baseline is measured from the TOP BORDER edge, so the
`margin_top` the old code added did not belong there either.

**Auto margins centred a box on an axis with no definite size.** The `<dialog>`
UA sheet pins all four edges with `margin: auto` and `width`/`height:
fit-content`. An author writing `top: 80px; left: 80px; width: 240px` should get
a box centred horizontally (definite width) at top 80 (height is not definite).
The port split the slack on both axes and put the dialog 217px too low. The
comment above the code already said "a definite size"; the code never checked.

That last one is worth recording for how it was settled. CSS 2.1 §10.6.4 read
literally gives the equal split — my implementation was defensible from the
spec. **The corpus carries Chrome's own `getBoundingClientRect` output**
alongside each case, and Chrome and the reference both say 80. Three sources,
and the two that are not me agreed. Reading the reference then showed the rule
stated outright: auto-margin centring applies only when that dimension is
definite, "excluding auto/fit-content/min-content/max-content, which per the
spec leave the auto margins as 0".

#### The honest scoreboard

"20/47" undersells it in one direction and oversells in another, so both
numbers:

* **26 of the 27 remaining failures use features that are not ported** — flex,
  grid, multicol, counters, list markers, quotes, `<br>`, containment,
  `word-break`. Phases 5–8. Their failing is the plan working.
* **Of the 21 cases that use only ported features, 20 now agree exactly.**

I got the classification wrong the first time and should not have: I read
`31-centered-modal`'s coordinates, saw an uncentred box, and called it an
absolute-positioning bug. It is a flex case. Two others I listed the same way
were flex and grid. Reading the coordinates instead of the stylesheet is exactly
the shortcut this harness exists to remove, and I took it while reporting the
harness's results. The classification is now scripted against the CSS.

The one real failure left is **`23-inline-splitting`**: the reference emits a
principal box per inline element (CSS 2.1 §9.4.2 — one fragment per line it
occupies), and the port collapses spans into raw items and never rebuilds them,
so `<a>` produces no box at all. The C# has a comment at the rebuild site
listing what breaks without it: paint cannot draw the span's background or
border, hit testing cannot surface clicks on it, and the DOM walk pairs every
following element against the wrong rect. That is a feature, not a fix.

### 22 of 23 in-scope cases, and a use-after-free the oracle flushed out

Phase 5 items, each pinned by the corpus:

**`<br>` did nothing.** It is an inline box with no children, so
`collect_recursive` recursed into it, found nothing, and the break was lost.
Now a forced-break item that ends the line and leaves a zero-width box on it,
taking the line's height — the reference emits one per break, and paint and hit
testing both expect to find it there rather than inferring a break from a gap.

**`word-break: break-all` did not break.** Every character boundary is a break
opportunity, so a long word is placed a slice at a time: fill the line, wrap,
repeat. Slices are views into the same source buffer, so no string is built.
`overflow-wrap: anywhere` is folded in with it — the two differ only in
min-content sizing, which is not tracked yet, and the reference makes the same
simplification and says so.

**A shorthand in an inline `style` attribute was ignored.** Stylesheet rules go
through `expand_declarations` once at compile time; inline styles never reached
it. So `style="margin: 0"` set a `margin` slot nothing reads, while the UA
sheet's already-expanded `p { margin: 1em 0 }` longhands kept the element — and
every `<p style="margin:0">` in the corpus sat 16px too low. `margin-top: 0`
worked, which is what made the bug invisible: the failing shape was the
shorthand specifically.

That last one had nothing to do with the phase it was found in. It is a cascade
bug, in code signed off in Phase 3, surfaced by a Phase 5 corpus case.

#### The regression test found a heap-use-after-free

Adding a `<br>` test to the C++ suite made ASan abort — not on the new code, on
`layout_inline_items`, which holds `const Box& cbox` across `flush_line`.
`BoxTree::create` appends to a vector, so every box reference dies at the next
create, and flush_line creates a line box plus a run per fragment. The reference
was valid until the first line was flushed; the SECOND line of any container
that had grown the vector past a reallocation was reading freed memory.

**Pre-existing, and latent for the whole port.** Every multi-line container was
exposed; the existing tests never happened to hit a reallocation at the wrong
moment. Forced breaks flush more lines, and it fired immediately. Fixed by
copying the style pointer out — it is owned outside the tree — rather than
reading through a reference that a create can invalidate.

Worth stating plainly: 7,180 checks, four toolchains and ASan+UBSan+LSan had all
been green over that bug for the entire port. It took a corpus case to write a
test that stepped on it.

#### Where the corpus stands

**22 of the 23 cases that use only ported features now agree exactly**
(22/47 overall). The 25 remaining failures all need flex, grid, multicol,
counters, list markers, quotes or containment — Phases 6–8.

The single in-scope failure left is `23-inline-splitting`, and it is a feature:
CSS 2.1 §9.2.1.1 block-in-inline splitting, where a block inside an inline
inside a block splits the paragraph into three boxes, plus §9.4.2 inline
fragments so the `<a>` produces a box per line it covers. Chrome's own capture
in the corpus shows the six boxes expected; the port produces four.

### Inline fragments: every in-scope corpus case now agrees

**24/47, and zero in-scope failures.** Every case that uses only ported features
matches the C# reference exactly.

The last one was CSS 2.1 §9.4.2. Block-in-inline splitting turned out to be
*already correct* — the port produced the right four boxes with the right
geometry — but an inline element produced no box at all. The port flattens
inline subtrees into text runs tagged with their originating element, and
`BoxTree::clear_children` then orphans the inline boxes. The C# has a comment at
the site where it rebuilds them listing what breaks without it: paint cannot
draw the span's background or border, hit testing cannot surface a click on it,
and a DOM walk pairs every following element against the wrong rect.

Three pieces:

* **A start marker per inline box.** Spans are usually derivable from the items
  inside them, but not always: an `<a>` whose only child is a block that
  block-in-inline splitting moved into a sibling has no items left here, and the
  reference still places it — zero width, at the pen where it began. A
  zero-width marker item records that point.
* **Markers survive the trailing-space trim.** A marker after a trailing space
  has to end up at the *trimmed* pen. The reference puts that empty `<a>` at
  x=36, hard against "Click", not at 43.2 where the removed space would have
  left it.
* **Spans accumulate over the inline ANCESTOR chain**, so nested inlines each
  get a box and the outer encloses the inner. The chain is walked through the
  tree because it is still intact at flush time — `clear_children` runs once, at
  the very end, and only detaches the container's direct children.

Fixing this also fixed `44-counters`, whose remaining difference was the same
missing boxes.

#### The first version of the regression test was worthless

It asserted on `find("s")`, which walks the tree for a box whose element has
that id — and **text runs carry their originating element too**, and precede the
inline boxes in a line's child list. So the test was measuring the text run. Its
x, width and height all happened to match what the inline box should have, and
it passed with the feature reverted.

Caught by deliberately disabling the attachment and re-running, the same check
used on the atlas-texture test earlier. The suite now has `find_kind`, which
takes a `BoxKind`, and the tests fail 8 checks with the feature removed.

That is twice now that a test written to pin new behaviour did not actually
exercise it. Both times the check that caught it was the same: break the thing
on purpose and confirm the test notices. It is cheap and it should be the
default for any test written to pin a fix.

## Phase 5 — Text (~9k LOC, highest uncertainty)

Decide `FontInterface` implementation (FreeType+HarfBuzz vs Godot `TextServer`)
**at the start of this phase, not before** — Phase 4 will have clarified how
much atlas control the paint layer needs.

`Runtime/Layout/Text` (1,501) + the shaping/atlas layer. Note the C# original is
not the reference here: its rasterizer reaches into Unity internals by
reflection and is being replaced, not translated.

**Exit:** `corpus/text/` green on line-break positions, line counts and line-box
heights (tolerance per ORACLE.md); surrounding box geometry stays zero-tolerance.

### Phase 6, first slice: single-line flex. 24 → 36/47.

Scoped deliberately: **`flex-wrap` is not ported**, so every container lays out
as one line, and `flex_wrap_is_ported()` returns false so a caller can refuse
rather than be quietly wrong. Everything the corpus uses is here — direction,
gaps, grow/shrink/basis, justify-content, align-items including baseline,
`order`, and the `flex` shorthand.

That scope was not a guess. Grepping the corpus for what the flex cases
actually declare showed **no `flex-wrap` at all**, so a single-line
implementation was the whole addressable set. ~350 lines against the C#'s 3,261
— most of that difference is wrapping, multi-line cross sizing, and the parts of
`align-content` that only exist once there is more than one line.

**All 11 remaining corpus failures need grid, multicol, containment, quotes or
list markers.** Nothing flex-shaped is left.

Four bugs found by grading each step against the reference:

* **`flex: 1` expanded wrongly at first.** The one-number form sets the basis to
  **0**, not auto — which is what makes three items share space equally
  regardless of content. `flex: 1px` means the opposite (basis 1px, grow 1). A
  bare number and a bare length take different branches and it is easy to write
  one rule for both.
* **An `position: absolute` child counted as a flex item**, because the filter
  read `Box::position`, which `apply_box_model` only stamps once layout runs —
  and layout had not run yet. Its width ate a share of the free space and three
  `flex: 1` cells came out 126.67 wide instead of 142.67. The style has to be
  read directly.
* **A non-stretched item filled its container on the cross axis.** §9.4 sizes it
  to its content instead. A column container's `align-items: center` item was
  coming out full width and then being "centred" with nowhere to move.
* **A stretched item was stamped, not re-laid.** Its content never saw the new
  size. Invisible until something inside depends on it — a nested column flex
  container, whose main size IS that height, had nothing for its
  justify-content to centre in. Fixed with `relayout_at_size` plus a
  `cross_size_imposed` flag so the auto-height rule does not collapse the
  imposed value straight back.

The same flag fixed `position: fixed; inset: 0` flex overlays, whose height is
only known after the positioning pass: that pass now re-lays the content at the
pinned size instead of stamping the height and moving on.

### The same use-after-free, for the third time

`Box& b = (*tree)[id]` held across a call that lays out a box. `BoxTree::create`
appends to a `std::vector<Box>`, so every reference into it dies at the next
create. Three sites now: `layout_inline_items`, `apply_absolute`, and the new
flex code — where ASan caught it, and where it had also produced a visible
wrong answer first (a fixed overlay dropped to its static position because a
stale `offset_top` was read).

It is not a discipline problem any more. Every access is by index —
`operator[]`, `size()`, `valid()`, the sibling chain — and **nothing takes
`.data()`**, so the storage does not need to be contiguous at all. Replacing the
vector with chunked storage that never moves an element would remove the class
outright.

The obvious `std::deque` swap conflicts with the zero-allocations-per-frame
target, because `reset()`'s `clear()` would free the blocks and the next frame
would re-allocate them. The version that keeps both is a deque plus an explicit
live-count, with `reset()` setting the count to zero and `create()` reusing
slots — exactly the arena discipline the current `reset()` comment already
describes, just with stable addresses.

**Not done in this tick, deliberately.** It is a change to the hottest data
structure in the engine, made at the end of a long session, and it deserves its
own pass with a perf measurement rather than being bolted onto the flex work.
The three known sites are fixed; the hazard is recorded here so the next tick
starts with it.

## Phase 6 — Flex (~4k LOC)

`Layout/Flex` (3,975). Port the documented deviations deliberately — including
the two RmlUi independently arrived at (bare text does not form anonymous flex
items; stretched items are not re-flowed internally) — rather than silently
fixing them, so the oracle stays meaningful. Fix them later, in both engines, as
a conformance change.

**Exit:** `corpus/flex/` diff clean.

## Phase 7 — Grid and subgrid (~5k LOC)

`Layout/Grid` (4,848). The hardest algorithm in the port and the single largest
differentiator — RmlUi has no grid, GTML has no grid, and it is why this project
exists rather than adopting one of them.

**Exit:** `corpus/grid/` diff clean, including `repeat()`, `minmax()`,
`fit-content()`, `auto-fill`/`auto-fit`, named lines and subgrid.

## Phase 8 — Remaining layout (~8k LOC)

`Positioning` (2,603), `Scrolling` (4,071), `Tables` (1,431),
`AnchorPositioning` (1,018), `Multicol` (472), `Floats` (216), `Containment`.

**Exit:** the full corpus diffs clean. **At this point the engine is at parity**
and everything after is host and performance work.

## Phase 9 — Godot host

GDExtension: `WevaDocument` as a `Control`, the ~20 bound types from §4 of the
feasibility doc (`Document`, `Element`, `Node`, `TextNode`, signals), input via
`Input`/`InputEvent`, clipboard and IME via `DisplayServer`, images via
`Image`/`ImageTexture`, resources via `ResourceLoader`.

Read [Godot-RmlUi](https://github.com/ashifolfi/Godot-RmlUi) first — MIT, 19
commits, and it implements exactly this plumbing.

**Exit:** the demo runs in Godot from GDScript with no C# anywhere.

## Phase 10 — GPU backend and shaders

Port the batched renderer as *one* backend: `MultiMeshInstance2D` + an RGBAF
data texture read via `texelFetch` (Godot's shading language has no
`StructuredBuffer`), or `RenderingDevice`/GLSL if profiling demands it. HLSL →
Godot shading language for the 7 shaders + `UIShaderLib.hlsl` (3,904 lines).

Clipping ports cleanly: the batched backend already abandoned stencil for
per-instance `clipRect` (slot 13) and clip-path SDF shapes (slots 16–20), and
Godot 2D exposes no canvas stencil — the codebase sidestepped its own blocker.

**Exit:** perf parity or better against the C# figures, with the allocation
target that C# could not reach: **zero heap allocations per frame in steady
state** (C# baseline: 1.42 MB/call layout, 1.10–2.19 MB/call paint).

## Phase 11 — C ABI and editor plugin

Freeze `weva_c.h`, version it, and only then consider the Unity P/Invoke shim.
Editor plugin (preview panel, DOM/style inspector, importers) — ~5,910 LOC of
C# editor tooling to re-express as a Godot `EditorPlugin`.

---

## Effort

The core (Phases 1–8) is ~65k LOC of translation. Sustained mechanical
translation of intricate, spec-driven code runs perhaps 500–1,500 LOC/day
including debugging, which puts the core alone at ~100–200 person-days — and on
a layout engine the differential-debugging tail is historically where schedules
go. **12–24 person-months to parity**, with the host, GPU backend and editor
plugin on top.

Treat any estimate below that with suspicion, including one that arrives after
Phase 4 goes surprisingly well. Phases 5 and 7 are where the variance lives.

## Kill criteria

Worth agreeing in advance, because sunk cost is the real risk on a port this
size:

* **Phase 4 exit slips badly.** Block and inline are the best-understood part of
  the engine. If they are hard, grid will be worse.
* **The oracle proves unmaintainable** — if corpus divergence is routinely
  "expected", the safety net is gone and the remaining phases are unguarded.
* **The differentiator stops mattering.** If RmlUi ships grid or container
  queries, re-run the §8 decision honestly rather than finishing out of momentum.

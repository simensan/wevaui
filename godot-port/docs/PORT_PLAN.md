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

The Forms layer implements `:required`, `:optional`, `:read-only`,
`:read-write`, `:default`, `:in-range`, and `:out-of-range`. Preview131 adds
`:valid` and `:invalid`, including form ownership and fieldset aggregation.
Applicable nonempty pattern constraints remain explicitly unresolved with a
matcher diagnostic; user-validity selectors still need interaction semantics.
See FORM_STATE.md for the browser-tested scope and limitations.
Live modal/popover state supplies `:modal` and `:popover-open`.
The subsequent top-layer work promotes live dialogs/popovers and backdrops to
document-root siblings. Painting and hit testing share opening order above author
stacking contexts; versioned form-state mutations rebuild the affected inputs.
Modal input isolation and focus restoration use a separate document modal stack.
Partial layout transactions preserve boxes promoted out of retained DOM parents.
See `FORM_STATE.md` for current verification and the
remaining contenteditable editing limitation.

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
  gate their bodies.** Size `@container` queries now use layout-owned,
  versioned per-element inputs and settle nested changes in the same update.
  Named/unnamed queries, ranges, relative lengths and the `container` shorthand
  are supported. Style/scroll-state queries and container-relative length units
  remain unsupported. See the Frontier Chrome parity report for verification.
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

### The allocation target was never measured, and it is being missed by a mile

`tools/weva_bench` times a layout pass and counts the heap allocations it makes,
by replacing global `operator new`. Both numbers are things this plan asserts
and nothing was checking.

The zero-allocations-per-frame target is not a nice-to-have here: it is one of
the **stated justifications for the port**, against a C# baseline of 1.42 MB per
layout call. The first measurement:

```
randhtml.html   1691 boxes   best 7.62 ms   steady-state allocations 74914 (10.9 MB)
```

**10.9 MB per layout pass.** Not near zero — roughly eight times the C# figure
the port set out to beat. The target has been in the plan since Phase 0 and had
never been run.

Attributing it took ten minutes with a scoped counter inside
`parse_css_value` (instrumentation since reverted; the method is a depth flag
plus an `operator new` that checks it):

```
parse_css_value: 8791 calls, 72319 allocations (97%), 10.7 MB (98%)
```

**97% of allocations are one thing: re-parsing CSS text during layout.**
`resolve_length` takes a `std::string_view` of the raw declaration and calls
`parse_css_value` every time — so every `width`, `height`, `margin-*`,
`padding-*` and `flex-basis` read re-tokenises and re-allocates a value object,
several times per box because shrink-to-fit probes lay a box out repeatedly.

The reference does not do this. The C# reads `style.GetParsed(propertyId)` — a
parsed value cached on the computed style — which is visible in
`PositioningPass.IsBorderBox` and elsewhere. The port kept the string
representation and pays for it on every read.

This reframes the perf work. It is not a matter of tuning a data structure; the
value path re-does at layout time what the cascade already did. The fix is the
reference's: cache the parsed value per property on `ComputedStyle`, or memoise
`parse_css_value` on the value's stable address and hand back a borrow.

**Deferred to the next tick on purpose.** It changes the ownership contract of
`parse_css_value` — callers currently take a `CssValuePtr` — and doing that at
the end of a session, on the path every layout number depends on, is how a
measured problem becomes an unmeasured one. The benchmark is committed first so
the change has a gate to be judged against.

#### And it retires the change this tick was supposed to make

The previous tick queued replacing `BoxTree`'s `std::vector<Box>` with chunked
storage, to kill the use-after-free class for good. The measurement says the
motivation for hurrying was wrong: arena growth is amortised to nothing and does
not appear in the steady-state count at all, so there was never a perf argument
either way. The correctness argument stands entirely on its own, which makes it
a smaller and safer change than it looked — and a lower priority than 10.9 MB a
frame.

### The parsed-value cache: 2.1x faster, 5.8x less garbage

`ComputedStyle` now memoises the parsed form of each slot, invalidated on
`set`/`unset`/`clear`. The header had this deferred with "revisit once the
cascade is running and there is something to measure" — `weva_bench` was that
measurement, and the answer was 10.9 MB a frame.

```
                 before      after
time (best)     7.62 ms    3.57 ms    2.13x
allocations      74,914     25,035    3.0x fewer
bytes           10.9 MB    1.88 MB    5.8x less
```

An inherited property shares the ancestor's cache entry rather than being
parsed once per element, and a failed parse is cached as a failure so a
malformed declaration costs one parse rather than one per read.

`box_sides` also stopped building four `std::string` concatenations and doing
four registry lookups per call — for margin AND padding on every box. The ids
are computed once per shorthand, which is the same fix the logical-property
tables already carry.

#### The attribution was wrong twice before it was right

The first pass converted `resolve_length`'s call sites and moved the needle by
**3.5%** — 74,914 to 72,296. The 97% figure was real but the conclusion drawn
from it was not: `parse_css_value` is **recursive**, so the depth-flag counter
attributed nested parses to their top-level call and the per-site call counts
did not add up to the total. The sites converted were not the hot ones.

Re-measuring per call site found the real distribution: `font_size_px` at 2,567
top-level parses per pass, `line_height_px` at 712, flex's number reads at 486.
`font_size_px` alone is called several times per box AND resolves its parent
recursively. Converting those three took the pass from 7.6 ms to 3.7 ms in one
step.

The lesson is not "measure" — that part went right. It is that an attribution
can be arithmetically correct and still point at the wrong fix, and the tell was
available: the per-site counts summed to half the total, and I should have
chased that gap before converting anything.

#### The test was wrong too, in both directions

The first version asserted the re-parsed value had a **different address** than
the old one. It does not have to: the old parse is freed and the allocator may
hand the same block back. That assertion passed for the wrong reason on one
build and failed for the wrong reason on the next. It now asserts on the value.

It also assumed `"!!!"` fails to parse. It does not. Removed rather than
papered over.

Verified by deleting the invalidation in `set()` and confirming the test fails —
which is now the standing rule for any test written to pin a fix.

Remaining: 25,035 allocations a pass, still ~90% inside `parse_css_value`. The
zero-allocation target is not met and should not be described as close. What is
left is the cascade's own value handling rather than layout's re-reads, and it
needs the same per-site attribution before anything is changed.

### Allocation profiling, and the target is met

`weva_bench --profile` records a backtrace per allocation and groups by call
site. It exists because the hand-placed counters of the previous tick drew the
wrong conclusion: `parse_css_value` is recursive, so a depth flag charges nested
parses to their top-level call and the per-site counts do not reconcile with the
total. A backtrace reconciles by construction. Frame pointers come from an
opt-in `WEVA_PROFILE_FRAMES` build so the shipped library carries no cost.

```
                 baseline    parsed cache   token reuse   ids actually used
time (best)       7.62 ms        3.57 ms       3.47 ms         2.50 ms
allocations        74,914         25,035        16,131           2,595
bytes             10.9 MB        1.88 MB        553 KB          184 KB
```

**3.0x faster, 28.9x fewer allocations, 59x less garbage.** The C# baseline the
port set out to beat is 1.42 MB per layout call; this is 184 KB, about 7.7x
better. The target the plan has carried since Phase 0 is met on this document.

Two changes after the parsed-value cache:

* **The tokenizer's buffer is reused across parses.** A fresh
  `std::vector<CssToken>` doubles 1→2→4→8 on every call, and declarations are
  short enough that the growth *is* the cost. One buffer per nesting level,
  since a nested parse must not reset the one its caller is reading.

* **The box-side ids were being thrown away.** This is the one worth recording.

#### A field added, a construction site missed

`box_sides` was changed last tick to carry the longhand ids so callers could
read the parsed cache. It computed them correctly and then returned a **freshly
brace-initialised** `BoxSideValues` listing only the four strings — leaving the
new id fields at their `kCustomPropertyId` default. Every caller took the
uncached path, and `margin` and `padding` — read on every box, four sides each —
got no benefit at all.

Nothing failed. 7,257 checks stayed green, the oracle stayed at 36/47, the
numbers improved for other reasons, and the change looked like it worked. **The
profiler is the only thing that found it**: the top allocation sites all read
`resolve_box_sides_px → resolve_length_cached → parse_css_value`, and
`resolve_length_cached` only calls `parse_css_value` on the branch where the id
is absent. Fixing it took 16,131 allocations to 2,595 in one edit.

The general hazard: adding a field to a struct that is built with aggregate
initialisation anywhere. The compiler is happy, the new field is silently
default-constructed, and the only symptom is that an optimisation does nothing.
There was a second instance in the same function — a static cache returning a
reference into a `std::vector` that a later insertion could move — fixed with a
`std::deque`.

Remaining: 2,595 allocations a pass, now in `layout_inline_items`' per-call
vectors rather than anywhere near the value path. That is a scratch-buffer
change on `BlockLayout` and a separate piece of work.

## Phase 6 — Flex (~4k LOC)

`Layout/Flex` (3,975). Port the documented deviations deliberately — including
the two RmlUi independently arrived at (bare text does not form anonymous flex
items; stretched items are not re-flowed internally) — rather than silently
fixing them, so the oracle stays meaningful. Fix them later, in both engines, as
a conformance change.

**Exit:** `corpus/flex/` diff clean.

### Phase 7, first slice: the explicit grid. 38 → 41/47.

Scoped from the corpus, as flex was: track lists over `<length>`, `<percentage>`,
`auto` and `<n>fr` with `repeat(<count>, <track>)`; `grid-template-areas` with
`grid-area: <name>`; row-major auto-placement; gaps; stretch placement. NOT
ported and named in the header: `minmax()`, `fit-content()`, intrinsic tracks,
`auto-fill`/`auto-fit`, numeric line placement, spans, `grid-auto-flow: column`
or `dense`, subgrid, and the alignment families beyond stretch.
`grid_is_fully_ported()` returns false. ~380 lines against the C#'s 4,848.

**All six grid cases now agree.** Three bugs, each found by grading a step:

* **Leftover space did not stretch `auto` tracks.** `align-content` and
  `justify-content` default to `normal`, which for a grid behaves as `stretch`.
  A single auto column in an 800px container came out at its max-content width.
* **Items in FIXED tracks never had their box model resolved.** Only auto tracks
  called `layout_block` (to measure max-content), so an item in a `260px` track
  kept zero padding, border and margin — a sidebar's children were placed at its
  content origin instead of inside its 16px padding.
* **A scroll container forced its row to grow.** CSS Grid §6.6 gives it an
  automatic minimum size of zero; it scrolls rather than expanding the track. A
  552px row whose item held 604px of content came out 604 and overflowed its own
  grid.

### The oracle is a reference, not ground truth

`29-card-grid-3x2` failed on six values, and the C++ was right.

`aspect-ratio: 1` on a card in a 240px track: Chrome says the card is 240 tall,
the C++ says 240, **the C# says 242** — while simultaneously reporting a
container height of 530, which its own 242-tall cards cannot produce. The
reference is internally inconsistent there.

The corpus has carried Chrome's `getBoundingClientRect` capture beside every
case since before this port started, and this is what it is for. `run_oracle.py`
now **arbitrates**: on a difference, when Chrome's geometry matches the candidate
exactly and differs from the reference, it reports `REFERENCE BUG` instead of a
failure, and prints Chrome's value on every difference either way.

The arbitration is automatic rather than a hand-maintained exemption list, which
matters: a list goes stale silently and starts hiding real regressions. This
re-derives the verdict from the third source on every run, and refuses to
arbitrate at all unless the three element lists line up element-for-element.

ORACLE.md says tolerance is zero. That still holds — what changed is that a
difference now has three possible verdicts rather than two, and the third is
evidenced rather than asserted.

### A number that looks like a regression and is not

`weva_bench` on the demo document went from 2.50 ms to 10.04 ms — because the
box count went from **1,691 to 6,967**. That document uses grid in 28 places,
and those subtrees were previously laid out as plain blocks. Per box the cost
went *down*: 1.48 → 1.44 µs. Allocations per box rose from 1.5 to 2.2, which is
the new grid code's own per-call vectors and is the same scratch-buffer work
already queued for `layout_inline_items`.

### 45/47, and one line of inheritance that closed two cases

**Containment.** `contain: size` and `content-visibility: hidden` size a box as
though it had no contents, with `contain-intrinsic-size` supplying a substitute.

The first attempt skipped laying the children out, which is what "skips its
contents" sounds like — and it was wrong. Chrome and the reference both report
**normal rects for the whole subtree** of a `content-visibility: hidden` box;
what is contained is the box's own contribution, not its children's geometry.
The fix moved to `finalize_block_size`, one place, and the special-case branch
in `layout_content` disappeared entirely.

The value test matters too: `contain` is a space-separated list, so `contain:
strict` and `contain: layout size` both apply — but a plain substring search
reads `contain: inline-size` as size containment, which it is not. The check is
word-bounded, and there is a test for exactly that.

**Anonymous boxes inherit.** CSS 2.1 §9.2.1.1. An anonymous block is created
with a null style, so reading `line-height` off the container alone missed the
author's value and fell back to the font's metric height. One box in a nested
list came out 18.29 tall where `line-height: 1` asks for 16, and everything
below it shifted by 2.29.

That single fallback — the container's style, or its parent's when it has none —
closed **both** `45-list-markers` and `43-quotes-pseudo`. Neither is about list
markers or quotes: both documents simply put text and a block in the same
container, which is what creates an anonymous wrapper. The reference falls back
the same way and carries a comment saying so at the same spot.

Worth noting how little the case names meant here. Two cases named after
features that are NOT ported passed once an inheritance rule was fixed, because
their failures were never about markers or quotes at all. Classifying failures
by the features a document mentions is a heuristic for triage, not a diagnosis —
the same shortcut that had me call three flex cases positioning bugs earlier.

**45/47 agree, 1 reference bug, 1 differs.** The one is `39-multicol`, which
needs multi-column layout — a real unported feature and the last of them in this
corpus.

## Phase 7 — Grid and subgrid (~5k LOC)

`Layout/Grid` (4,848). The hardest algorithm in the port and the single largest
differentiator — RmlUi has no grid, GTML has no grid, and it is why this project
exists rather than adopting one of them.

**Exit:** `corpus/grid/` diff clean, including `repeat()`, `minmax()`,
`fit-content()`, `auto-fill`/`auto-fit`, named lines and subgrid.

### The corpus is clean: 46/47 agree, 0 differ

Multi-column layout was the last case. `column-count`, `column-width` (and the
used count derived from it), `column-gap`, and balancing. Not ported and named
in the header: `column-span`, `column-rule`, `column-fill: auto`, orphans and
widows, and — the significant one — **splitting a child across a column
boundary**. A child taller than the balanced height takes a column to itself and
overflows instead of fragmenting. `multicol_is_fully_ported()` returns false and
a test pins that limit so it stays a known edge rather than a surprise.

**Every case in the corpus is now accounted for**: 46 agree with the C#
exactly, and the 47th is the `aspect-ratio` reference bug where Chrome sides
with this port.

#### The test caught the algorithm

The balanced column height is **not** `total / count`. That is the lower bound,
and with whole children it is usually unreachable: five 10px children in two
columns give a bound of 25, but no column can be 25 tall, and filling to 25 puts
two children in the first column and three in the second — where a browser puts
three and two. Both arrangements are 30 tall, so **the container's height is
identical either way** and only the children's x coordinates differ.

The corpus case does not distinguish them — it is six equal children in three
columns, where the bound is reachable and both algorithms agree. So the oracle
was green on the wrong algorithm, and would have stayed green.

What caught it was writing the test first and finding it disagreed with the
code. The tempting move at that point is to change the assertion; working out
what a browser actually does showed the code was wrong instead. The real rule is
the smallest REACHABLE height that still fits in `count` columns, and the
reachable heights are the prefix sums of the children's heights, so it is a
short search rather than a division.

Recording it because the near-miss is the point: a green oracle means the corpus
does not distinguish the alternatives, not that the implementation is right.

### Widening the corpus, because a clean corpus proved nothing

`tools/oracle/harvest_corpus.py` extracts HTML+CSS fixtures from the C# test
suite's verbatim strings — ORACLE.md's first listed corpus source, and worth
doing precisely because the hand-built 47 cases had gone green. Multicol had
already shown what that is worth: a wrong balancing algorithm passed the corpus
because its one multicol case did not separate the two answers.

Classifying strings by CONTENT rather than by variable name is what made it
work: the suite writes `const string css`, `var css`, `string Css`, and passes
markup straight into a call unnamed. Matching on `const string` alone harvested
53 cases from 17 files; matching every verbatim string harvested **212 from 39**.

**First run: 120/212.** Three bugs so far, none reachable from the old corpus.

**Inline fragments were attached in the wrong order.** They went on after the
line's runs, so `<label>Name</label>` came out AFTER the `<input>` that follows
it in the source. Fragments are now attached where the box OPENS — but that
reparents the box onto the line, which severs the ancestor chain the next
fragment's span walks, so the accumulate and the attach had to become two
passes. `box.h` had documented `insert_child_first` for exactly this ordering
problem and nothing used it.

**`min-height` was read as a definite height.** The flex and grid dispatch asked
`!parent_height_auto`, which also answers "not auto" for a box constrained only
by `min-height` — and a min-height gives no USED height at that point, so the
container got an available main size read off an uncomputed field. It was zero,
and a column flex container shrank every item to nothing. `min-height: 100vh` on
a page shell is common enough that this alone was five harvested cases.

**An empty inline box.** `<div><section><span></span></section></div>` has to
produce a line box — the section takes the strut's height — and no fragment box,
because a fragment is earned by covering content. Both halves matter and pull
opposite ways: drop the line and the height goes; keep the fragment and the
element count is wrong. And an empty `<a>` on a line that DOES have content must
still appear, which is the block-in-inline case from `23-inline-splitting`. That
one rule took the harvested corpus from 122 to **140/212**.

**Where it stands: 140/212 harvested, 46/47 hand-built.** Of the 72 harvested
failures, **24 use only ported features** and are a real backlog — the largest
groups are selector-combinator and spatial-navigation cases. The rest need
`flex-wrap`, advanced grid, anchor positioning, transforms, counters and the
other features the plan already lists as unported.

### Chrome arbitrates the harvested corpus too: 140/212 → 171/210

The harvested corpus had no Chrome captures, so every disagreement counted
against the port — including the ones where the reference is wrong. The
capture script now takes a directory (`node Tools/Layout/capture-all-chrome-layouts.mjs
<corpus> 800 600`), and `run_oracle.py` arbitrates the harvest the way it
already arbitrated the hand-built cases. Two harness fixes came with it:

* The capture's `@font-face` path pointed at a directory the fonts had left,
  so Chrome had been measuring with the machine's sans-serif. Fixed to
  `Runtime/Resources/Fonts`. Text-line heights still do not arbitrate exactly —
  Inter's `normal` line height is 20 at 16px where the reference's synthetic
  metric gives 18.29 — but geometry does, which is what settled everything
  below.
* `--reuse-reference` skips BaselineGen when a reference dump is newer than
  its case. The .NET start-up per case was ten minutes of every run; the
  candidate side is five seconds. Run without the flag after a C# change.
* The harvester classified a string as HTML on angle brackets alone, so an
  `@property` block with `syntax: "<length>"` became two bogus cases whose
  elements were `<length>` tags. It now wants a closing or void tag. 212 → 210.

**Twelve reference bugs surfaced at once**, and the largest group was the
whole spatial-navigation family: the reference gives an absolutely positioned
`<button>` with `width: 80px` a width of **0**. Chrome says 80, the port says
80. The port had its own bug on the same cases — the button was one level
deeper in the dump — and that one was real.

**What the port got wrong, in the order it was found:**

* **An out-of-flow `inline-block` was not blockified.** §9.7 blockifies every
  inline-level display, and the box builder only did `inline`. A positioned
  button (inline-block by the UA sheet) stayed an atom, was placed on a line
  box, and read one level deeper than the reference on six cases.
* **Anonymous blocks read `text-align` off their own null style.** The
  line-height fallback to the parent already existed at the same spot; the
  alignment did not, so an inline-flex pill that shared a right-aligned
  parent with a block sibling was flushed left.
* **Flex `min-/max-width` were compared without the frame.** They are
  content-box sizes under the default sizing and the algorithm clamps the
  border box — flex-basis already had this correction, min/max did not.
  `min-width: 38px; padding: 0 10px` gave 38 where the answer is 58. Six
  cases.
* **Flex auto margins on the main axis were never resolved.** §9.5 step 1:
  free space goes to them before justify-content, split equally. The values
  are written back onto the boxes, after zeroing whatever block layout had
  resolved for the same margin.
* **A column item's flexed height was stamped, not re-laid.** §9.8 makes it
  definite for the item's own contents; without the relayout a nested row's
  stretched child was 0 tall inside a 180px row.
* **A column container's `min-height` never made its main size definite.**
  §9.2 step 4: content-sized, then clamped, and the clamped size is what the
  items flex into. The page-shell `min-height: 100vh` + `flex: 1` body was 0
  tall. And with the main size indefinite, a `0%` basis had been resolving
  against a basis of -1 — a present basis makes a percentage a length — so it
  is now passed as absent and falls back to content, per §7.2.3.
* **Grid read no line placement at all.** `grid-column: 3`, `1 / 3`,
  `span 2`, negative lines, the longhands and the four-part `grid-area` all
  auto-placed. §8.3 line resolution and §8.5 sparse placement are in,
  including the cursor rules (a column-locked item whose start is before the
  cursor drops a row) and implicit columns.
* **Grid had no alignment.** `*-items` / `*-self` / `*-content` are in for
  start, center, end, stretch and the space-* keywords; a non-stretched auto
  width fits its content, an explicit width is re-resolved against the cell
  instead of overwritten (the `place-items: center` modal was 800 wide), and
  `min-height` on the container is definite for `align-content` the way it
  is for flex.
* **Auto tracks stretched under `justify-content: start`** and ignored an
  item's `min-width`; an `overflow: hidden` item was left out of an auto row
  even in an auto-height grid, where the grid is sized under a max-content
  constraint and every auto track grows to its growth limit. The zero
  automatic minimum is right only when the height is definite.
* **An absolutely positioned auto-width box kept the full containing-block
  width** unless both horizontal edges were pinned. §10.3.7 says
  shrink-to-fit, and a `left: 0` label spanned the page.

**Where it stands: 171/210 harvested agree, 27 differ, 12 reference bugs;
46/47 hand-built.** Of the 27, two use only ported features and are the real
backlog: `minmax()` tracks (parsed as `auto`) and `aspect-ratio` on a grid
item stretched in both axes. The rest are anchor positioning (9), subgrid (5),
transforms (2), `flex-wrap`, `auto-fill`, `grid-auto-rows`, counters — or
cases the headless reference cannot lay out at all: form controls have no
boxes there, and an `<img>` with no `src` produces no line (Chrome and the
port both give the line its strut height; only the text metric keeps that
from arbitrating).

7,508 checks green across gcc 13 and clang 18 with ASan+UBSan. The Godot host
was not rebuilt this round — nothing under `hosts/` changed and no Godot
binary was available where this ran.

### The sample gate: the Unity pages, in Godot. 0 → 7/35

The goal past parity-by-corpus is the actual product: every page under
`Assets/UI` and `Packages/com.wevaui/Samples~` laid out by the port and drawn
by the Godot host. `tools/oracle/collect_samples.py` gathers the 35 into a
corpus (a page whose stylesheet is inline in `<style>` gets it extracted —
neither dumper reads `<style>`, and a page laid out unstyled on both sides
would agree for a worthless reason). Run at 1280×720 with Chrome captures
beside them. **First run: 0/35.**

The host side is now reproducible locally as well as on the cloud box: Godot
4.7.2 headless under WSL, `godot-cpp` at API 4.7, the extension built from
`hosts/godot`, 23/23 render tests. Under WSLg Godot gets a GL context from
llvmpipe, so `capture.tscn` renders a page to a PNG headlessly:

    godot --path hosts/godot/project --rendering-driver opengl3 \
          --scene res://capture.tscn -- --html menu.html --css menu.css \
          --size 1280x720 --png out.png

The first such render (menu.html) showed the card grid collapsed into one
row — `flex-wrap` is unported — which is the kind of thing the layout gate
below exists to find before a screenshot does.

**Fixed, in the order the smallest-diverging pages surfaced them** (each with
its own test; the oracle numbers are the sample corpus):

* `font` shorthand was never expanded; `font: bold 14px sans-serif` left a
  button's size at 16px.
* `max_content_width` took the widest child for every container; a flex ROW
  is the sum of its items plus gaps, and a block child contributes its
  explicit width or content + frame + margins, not its inner text alone. An
  absolutely positioned pill shrank to its amount and its icon was crushed.
* The positioning pass handled descendants before their ancestor; sizing the
  ancestor re-laid the descendant at the containing block's width.
* A stretched flex item's cross size is clamped by its own min/max; a row
  container with `min-height` is at least that tall.
* `letter-spacing` was registered and never applied. The reference's
  convention exactly: width + spacing × (UTF-16 units − 1) per run, spaces
  included.
* An inline fragment's height came from the root font size; inline boxes are
  never stamped with one. Fragments are inserted FIRST on the line as the
  reference's `InsertChildFirst` does, later-opened before earlier — not
  document order, but it is the tree the dump walks. A marker dangling at a
  line's end whose box has content later is carried to the next line.
* A shorthand carrying `var()` was left unexpanded for good;
  `border: 2px solid var(--cyan)` produced no border widths. Expanded after
  substitution.
* The font-family registry (`LayoutContext::register_font`, first registered
  head of the stack wins); `weva_dump` registers `monospace` as BaselineGen
  does, so `<code>` is 0.6em per glyph on both sides.
* Flex `center`/`end` are unsafe; a definite cross size IS the line's cross
  size; an aspect-ratio-derived height is definite; a grid item with a ratio
  stretches one axis and derives the other (block when the row is definite,
  inline otherwise); `cross_size_imposed` is per pass; a percentage height is
  definite only against a definite parent.

**Reference bugs the samples exposed** — each confirmed with a BaselineGen
mini-case and each with Chrome on the port's side. They are not mirrored, and
they are why several pages cannot reach "agree" without a C# fix:

* An absolutely positioned child takes flow space inside a positioned parent:
  the parent grows by the child's height and following siblings shift
  (`nook-dialogue`, `load-game`).
* A percentage width in a flex item's child resolves against the flex
  container, not the item (`hud` bar fills: 70% of `.bar`, not of `.bar-track`).
* `aspect-ratio` is applied to the border box under content-box sizing (an
  80px square with 1px borders is 82 tall; Chrome and the port: 80) (`vendor`).
* A column-flex card stretched in a row comes out shorter than its own
  children (`form-demo`: 1017 with content to 1266; Chrome 1580).
* A `1fr` grid item shrink-wrapped to its label (`9slice-demo`: 79.6; Chrome
  and the port 389.33).
* The centring of an aspect-ratio flex container's child is 2px low, as if
  against the border box (`dialogue`).

**Where it stands: 7/35 samples agree** (inputtest, story-bubble, todo,
episode-stats, sample-menu, particles, neon); dialogue, nook-dialogue,
vendor, form-demo, card-component and load-game are within 2–7 differences,
all but card-component's on the reference-bug list above. The harvested
corpus rose to 173/210 alongside and the hand-built stayed 46/47. 7,623
checks green on gcc 13 and clang 18 with ASan+UBSan; Godot host 23/23.

**Next, by pages unblocked:** `flex-wrap` (8 pages, and the menu render),
`minmax()` / `repeat(auto-fill|auto-fit)` (grid-playground, stock-dashboard,
UpgradeMeter), the template/`<slot>` component expansion the package sample
uses (`card-component`), then the C# fixes above so the reference stops
arguing with Chrome.

### Second pass: flex-wrap, the automatic minimum, and what the cascade owed. 7 → 9/35

* **`flex-wrap`.** `layout_flex` is now a list of lines (§9.3): items go onto
  a line while their outer hypothetical sizes plus gaps fit; flexing, auto
  margins, justify-content and baseline grouping run per line; align-content
  distributes a definite cross size (normal/stretch onto the lines, the
  space-* keywords as gaps); wrap-reverse stacks from the cross end. The menu
  page renders as a card grid in Godot instead of one squeezed row.
* **The automatic minimum size (§4.5), both axes.** A column item's
  `min-height: auto` is its content height, a row item's `min-width: auto`
  its min-content width, neither for scroll containers. Without it a
  `height: 100vh; overflow: auto` shell shrank its topbar to its padding and
  a quest card to 40px less than its own children; with the row half a
  1310px carousel keeps its width and centres at −15. `min_content_width`
  joins `max_content_width` on one intrinsic-size walk — and max-content of
  wrapped text is the paragraph rejoined from its lines plus the spaces the
  wrap trimmed, which is what let a centred paragraph fit its column instead
  of its widest line.
* **Negative free space** (§8.2): center/end overflow both sides / the
  start; space-around/evenly fall back to center, space-between to start.
* **`var()` shorthands expand at their cascade position.** Custom
  properties are applied first (the parent link borrowed for inherited
  ones), then the shorthand is substituted and expanded in the main pass —
  so `.row:last-child { border-bottom: none }` beats an earlier
  `.row { border-bottom: 1px solid var(--edge) }`, which the end-of-compute
  expansion had been overwriting.
* **`@container` follows the reference's single pass.** The C# evaluates
  container queries through a box-lookup hook that is null until a box tree
  exists, so in BaselineGen they never apply. Applying them unconditionally
  put a 22px h2 on every card the reference lays out at 18px. Chrome applies
  them; both engines need the layout-then-restyle loop before this can.
* **Inline `em` font sizes** resolve against the element's parent, not the
  root (`<small>` in a 14px label is 11.62, not 13.28).
* **Abspos children of a flex container** take their static position as if
  the sole item (§4.1) — centred by center/center — against the container's
  final size and with their shrink-to-fit width.
* **`weva_dump` mirrors `LayoutDump.ResolveTransformTranslation`**, so a
  `transform: translateX(-50%)` tooltip is compared where the reference
  reports it. Both TransformHitTest cases agree as a result.

**Where it stands: 9/35 samples agree** (inputtest, story-bubble, todo,
episode-stats, sample-menu, particles, neon, settings, level-select), plus
`map` arbitrated as a reference bug — Chrome sides with the port on its
card widths. quests, dialogue, nook-dialogue are at 2 differences, combat-hud
at 4, form-demo 4, vendor 6, load-game 7; all but combat-hud's on the
reference-bug list. Harvest 176/210, hand-built 46/47. 7,709 checks green on
gcc 13 and clang 18 with ASan+UBSan; Godot host 23/23.

**One more reference finding:** the C# has no automatic minimum size in
either axis — `flex-playground`'s `.body` (a `flex: 1` column item with
1.5k px of content in a 720px shell) is 657 tall there and 1579 in Chrome.

**Next:** `grid-template-columns: subgrid` (menu's `.sub`, five harvest
cases), `minmax()` / `auto-fill`, the template/`<slot>` expansion for
`card-component`, then the page-shaped remainders (inventory, stats, hud,
leaderboard) once the C# side stops disagreeing with Chrome.

### Third pass: the grid track model, and what an inline box owns. 179/210, 9/35 + 2

* **Tracks are min/max sizing functions** (§7.2.3) and `size_tracks` is
  §12.3–12.8: intrinsic bases and limits from the items' min-/max-content
  contributions (spanning items spread what the covered tracks lack,
  shortest spans first), maximize, the fr found by re-dividing around
  tracks whose base exceeds their share, stretch for auto-max tracks.
  `minmax()`, `fit-content()`, `min-content`/`max-content`,
  `repeat(auto-fill|auto-fit)` (auto-fit collapsing what nothing landed in)
  and `grid-auto-rows/columns` all parse and size. Three harvest cases and
  most of grid-playground (276 → 8), advanced-dashboard (161 → 5) and glass
  followed.
* **Inline boxes own their horizontal edges** (§10.6.1): start and end
  markers carry margin/border/padding, advance the pen, and give the first
  and last fragments their decoration; the intrinsic-width walk counts them
  and no longer counts the fragment boxes on top of their runs (every bold
  word had been doubled toward max-content).
* **Aspect-ratio grid items** take, per axis, the larger of their own
  stretch and the transfer from the other axis — the one rule that fits
  Chrome's 196×196, 195×195 and 80×80.
* **Chrome verdicts within 1/64px.** LayoutNG snaps; 103.01 against 103 is
  agreement. Only the arbitration reads this way; ref-vs-cand stays exact.

**Where it stands: 9/35 agree, glass and map arbitrated to the reference;
harvest 179/210 with 13 arbitrated; hand-built 46/47. 7,759 checks green.**
Still unported and now the largest remaining causes: `subgrid` (menu, five
harvest cases), the template/`<slot>` expansion (card-component), anchor
positioning (nine harvest cases), and the C# reference's own bugs behind
most of the sub-10-difference pages.

### Fourth pass: what the reference lists where, and what Chrome can decide. 11/35 + 5

Mostly about making the oracle able to say who is right, then acting on it.

* **Chrome measures with the engines' faces.** Two synthetic TrueType faces
  (`make_mono_font.py`: 0.45em / 0.6em advances, 0.85 / 0.293 vertical
  metrics, emoji at the engines' 1.3em / 1.0em) loaded by the capture script
  under `--metrics=mono`, `line-height: normal` pinned to 1.143 because Blink
  rounds a face's ascent and descent for `normal`. Text widths now agree to
  the hundredth, and every FAIL line says whose side Chrome takes. The faces
  are generated from the corpora's own character set — 87 code points on 20
  sample pages were falling back to a system font before.
* **Elements pair by identity, not index.** The engines list inline
  fragments in the C# `InsertChildFirst` order while Chrome walks the DOM,
  and Chrome omits `display: none`; either used to leave a whole page
  unjudged. Longest-common-subsequence first, leftover same-identity
  elements as moves; a shift from an inserted neighbour is not a move.
  Seven sample pages gained verdicts; stock-dashboard's `<b class=up>`
  turned out to be the reference's "empty inline → insertion point" fallback.
* **Block-in-inline splitting** (§9.2.1.1), **subgrid** hand-off,
  **row-reverse** packing from the main end, **wrap-reverse** flipping item
  alignment (an inapplicable `stretch` is `flex-start` and flips too),
  top-level `min()`/`max()`/`clamp()`, a grid container's height being its
  tracks' extent, and its intrinsic width being its tracks' (an 8-column
  board centred in a flex row was shrink-fitted to one tile).
* **Auto margins are not intrinsic width** — a resolved `margin: auto`
  is leftover space from the last layout, not content.
* **Aspect-ratio relates the boxes box-sizing names** (css-sizing-4 §5.1),
  in both directions. hud's 3/4 portrait: 383.33 under content-box, as
  Chrome and the reference both say; the port had 384. The one test that
  pinned the border-box shortcut for width-from-height was recalibrated to
  Chrome (220, not 200).
* **Baseline-aligned flex items size the line** (§9.4 step 8): largest
  ascent plus largest descent. layout-stress's topbar went from 718
  differences to 8 on that alone.

Tried and reverted: letter-spacing after every character (Blink) instead of
every character but the last (both engines). Correct, but on pages where
Chrome cannot arbitrate text it only moves the port away from the reference
— 11/35 became 4/35. Kept as a documented shared deviation.

### Fifth pass: tables, and Chrome under the engines' UA sheet. 12/35 + 9

* **Table layout** (CSS 2.1 §17, separated borders) as the reference has
  it: colspan/rowspan placement, `table-layout: fixed` from `<col>` hints
  and first-row authored widths, the reference's automatic layout, border-
  spacing, header → body → footer order, captions, `vertical-align`,
  `visibility: collapse`. Dispatched in flow like grid. leaderboard: 122
  differences → 0.
* **Chrome lays out under the engines' UA sheet** (`@layer weva-ua`, first
  in `<head>`, full documents included) — `html, body { margin: 0; height:
  100% }`, the form-control and heading defaults. layout-stress and vendor
  arbitrated outright; randhtml 35 → 5 unjudged.
* **Lean verdicts.** Chrome within 1.5px of the port and whole pixels from
  the reference is a `REF~`; nook-dialogue and stock-dashboard. Same-
  identity runs pair by whichever of three pairings gives Chrome the most
  agreements with either side.

**Where it stands: 12/35 agree, nine more arbitrated to the port (seven
exact, two leaning); harvest 180/210 with 16 arbitrated; hand-built 46/47.
7,908 checks green on gcc 13, clang 18, ASan+UBSan.** Left on the samples,
all of it either the reference's own bugs Chrome cannot confirm or shared
engine-vs-Chrome divergences: `@container` (menu — never applied in the
single-pass BaselineGen, nor here), the `<template>`/`<slot>` expansion
(card-component), form-control intrinsic sizes (form-demo), Chrome's
re-growth of `1fr` tracks from aspect-ratio items' transferred minimum
(inventory, stats), an `<img>` the reference wraps one level deeper
(advanced-dashboard), and per-page inline-rect and text-width residue
(audit-validation, weva-landing).

### Sixth pass: the port opens on Windows, and pages get their paint. 12/35 + 10

* **Stretched rows feed back into `1fr` columns** (§12.1 steps 3–4) for
  aspect-ratio items — but only into columns with an intrinsic minimum; a
  fixed column settles the inline size first. stats REF~, vendor REF!, a
  harvest case REF!. The 119/196 test had pinned the reference's
  non-regrowing columns; Chrome regrows (second square at 204).
* **Windows build.** MSVC branch for the load-bearing flags; the core's
  7,9xx checks pass under MSVC unchanged. The host needs godot-cpp's
  `/Zc:__cplusplus` and `/vmg`, the static CRT to match godot-cpp, and
  per-configuration output directories. `weva_godot.dll` runs the host
  suite 23/23 under Godot 4.7.1 win64 and `capture.tscn` renders through
  the GPU.
* **Backgrounds.** Until now paint knew `background-color` and the
  `background` shorthand expanded to nothing, so every sample page rendered
  on white. Now: gradients (linear / radial / conic, repeating, hints,
  double stops, premultiplied sRGB) rasterized with their
  position/size/repeat into one texture per box and drawn on the rounded
  rect; the root/body background on the canvas (§14.2); the shorthand's
  five longhands; transient textures owned by the document across the ABI,
  mirrored by id in the host. Line and anonymous boxes stopped painting
  their container's decorations.

**Where it stands after the sixth pass: samples 12/35 agree + 10
arbitrated; harvest 179/210 + 17; hand 46/47; 7,980 checks green on gcc 13,
clang 18, ASan+UBSan and MSVC 14.44; host 23/23 on Linux and Windows.**

### Seventh pass: text through the host, and the rest of the box's paint. 12/35 + 11

* **Fallback fonts in the host.** A face is a list of TextServer fonts (the
  theme font, then a SystemFont over the platform's symbol and emoji
  faces); shaping runs across the list and a glyph id carries its font in
  its top byte. ★, →, ⚔ and the emoji icons draw (colour emoji as
  silhouettes — the atlas is coverage-only).
* **Letter-spacing in paint.** Layout had sized every word with it and
  paint drew the glyphs without, so words came out narrower than their
  boxes and the gaps read as doubled.
* **A 1e-9 fit tolerance on the wrap test.** A shrink-fitted box is re-laid
  at the sum of its run widths and the pen re-adds them in another order;
  with a real face's float advances the last word landed an ulp past the
  line ("View Full Ladder" on two lines). 9slice-demo's `<code>` was the
  same, and is now arbitrated to the port.
* **box-shadow, overflow clipping, opacity, visibility.** Shadows as
  Gaussian-profiled nested shapes (no blur pass in the canvas); clipping as
  a rectangle to the padding box; opacity as vertex alpha (no group layer
  yet).
* **Scissors are applied to the geometry.** Godot clips per canvas item,
  and the compatibility renderer dropped every draw after a clipped sibling
  item (bisected with env switches on the host); the collecting backend
  now cuts scissored triangles itself, so any host draws what it is given.

**Where it stands after the seventh pass: samples 12/35 agree + 11
arbitrated; harvest 179/210 + 17; hand 46/47; 8,005 checks green; host 23/23
on Linux and Windows.**

### Eighth pass: all 35 samples on Windows, and what they showed. 12/35 + 11

A contact sheet of every sample through the Windows host, then the gaps in
the order they were visible, weighted by how many pages use each:

* **`text-transform`** was unimplemented (51 uses). Applied when the run's
  Text box is built, the tree owning the string; the mono-metric oracle
  cannot see it, a real face's capitals are wider.
* **CSS transforms** (190 uses, 30 pages): translate/scale/rotate/skew/
  matrix with `transform-origin`, as a matrix stack every draw of the
  subtree goes through; a clip under a transform is the transformed box's
  bounding box.
* **`text-shadow`** (34): offset copies; a blur is a 5x5 Gaussian kernel of
  glyph copies.
* **`background-clip: text`**: the gradient reaches the glyph vertices
  (weva-landing's headline).
* **`filter: blur()`** (28): background and rounded shape rasterized with
  room, blurred, drawn as one texture; children sharp (neon's blobs).

**Where it stands after the eighth pass: samples 12/35 agree + 11
arbitrated; harvest 179/210 + 17; hand 46/47; 8,021 checks green; host 23/23
on Linux and Windows.**

### Ninth pass: Godot beside Chrome, page by page. 12/35 + 11

The capture script grew `--screenshot` / `--no-layout`, and every sample was
put beside its Chrome screenshot. Most pages read the same; the difference
on EVERY page was weight: the host had one face, so every heading, `<b>`
and `<strong>` drew regular.

* **Bold and italic through the host.** `variant(face, weight, italic)` in
  the font ABI; layout measures a 600+ or italic run with the variant's
  metrics and paint draws it with the variant face; the glyph atlas is keyed
  by face. The Godot host builds variants as independent fonts from the
  theme FontFile's data (embolden, shear) — a linked variation shares the
  glyph cache, and the whole page came out bold italic.

**Where it stands: samples 12/35 agree + 11 arbitrated; harvest 179/210 +
17; hand 46/47; 8,030 checks green; host 23/23 on Linux and Windows.** Next
from the side-by-sides: form controls (inputs collapse to lines, selects show
every option, no placeholders, no range thumb), rounded clipping and
`clip-path` (combat-hud's circular minimap and hex icons, story-bubble's
notch), `::after` badges to verify, generic families (`serif`, `monospace`)
mapped to system faces, inline-style custom properties (level-select's road
colours), colour emoji, `url()` images.

### Tenth pass: the boxes nobody was generating. 12/35 + 11

The form-control audit found something larger than form controls: the port
cascaded `::before` / `::after` styles but never built their boxes, and
BaselineGen — the oracle's reference — omitted both the pseudo resolvers
and FormControlStylesheet, so ref and cand agreed on a shared omission.
Every badge, quote mark, counter prefix and decorative overlay in the
samples had been missing on both sides of the diff.

* **Pseudo-element boxes.** `StyleProvider::pseudo_style_of`; the builder
  injects the boxes as first/last children (blockified when abspos/floated,
  items in flex/grid), with `pseudo_host` on the Box so paint decorates
  them while the dump skips them. Counters and quote depth are tracked in
  the builder's own document-order walk (`counter-reset/-increment/-set`
  scopes, `counter()`/`counters()` in decimal/alpha/roman, `open-quote`
  through the `quotes` pairs). Two cascade bugs surfaced only once boxes
  existed: the pseudo inherited nothing declared above the host
  (`contains()` sees own slots; read through the inherit chain), and its
  var() namespace held only the host's own tokens (combat-hud sets
  `--icon` on the `<li>`, the pseudo hangs off a child).
* **Form controls.** The UA sheet gained the FormControlStylesheet rules
  (218×34 inputs, `option { display: none }`, the range track) plus Chrome's
  `input[type=hidden] { display: none }` — added to the C# sheet too, it laid
  out as a text field. Paint draws the runtime's overlays: value /
  placeholder / password bullets / the chosen option, check and radio
  marks, the range rail + thumb, the select caret. BaselineGen now mirrors
  UIDocumentBuilder (form sheet, Before/AfterStyleOf), and weva_dump /
  weva_bench cascade the pseudos.
* **Geometric clipping.** `clip_triangles_polygon` (ear clipping, concave
  allowed) and `rounded_rect_outline`; paint carries a ClipNode chain so
  `clip-path: polygon/circle/ellipse/inset` clips a subtree and a rounded
  `overflow: hidden` cuts its corners. combat-hud's hexagons and round
  minimap, the avatar circles.
* **color-mix()** evaluates (premultiplied, sRGB), and gradient stops take
  a `calc()` position — the progress-ring and HP-fill idioms both depended
  on those.
* **Three more from the side-by-sides.** Inline custom properties are
  seeded before the cascade's early shorthand var() substitution
  (level-select's `style="--bg:…"` discs took the fallback); clip polygons
  live in screen space so a rotated road is clipped where it lands inside
  its round map; and children paint in Appendix E order per container
  (negative z, in-flow, positioned z:auto, positive z) — the ring number
  under its `::after` cover, badges under later siblings.

**Where it stands: samples 12/35 agree + 11 arbitrated (unchanged — both
sides gained the same boxes); harvest 178/210 + 18; hand 46/47 (43-quotes
and 44-counters now agree); 8,320 checks green; host 23/23 on Linux and
Windows; form-demo renders every control, level-select and story-bubble
read like Chrome.** Still open from the
side-by-sides: colour emoji (the host copies alpha out of Godot's glyph
texture; a colour glyph needs an RGBA atlas), `mask-image` fades,
`backdrop-filter` (10 samples), an opacity group layer, `url()` images and
`border-image`, `@container`, and the shared engine-vs-Chrome divergences
the oracle lists (form-demo's control heights, inventory's 1fr regrowth).

### Eleventh pass: colours the text colour must not touch. 12/35 + 11

* **Colour glyphs.** A Bitmap may carry straight-alpha RGBA beside its
  coverage (`weva_glyph_bitmap.rgba`, appended for ABI stability); the
  atlas keeps a colour glyph's texels and paint draws that quad white with
  the text's alpha (CSS Fonts 4 §5.2). The Godot host copies RGBA out of
  TextServer's RGBA8 glyph pages when the texels carry chroma — and the
  real bug behind blank emoji was the fallback list: one SystemFont with a
  NAME LIST resolves to its first match only, so "Segoe UI Emoji" behind
  "Segoe UI Symbol" was never reached. One SystemFont per installed name
  now (checked against `OS::get_system_fonts`, so a missing name cannot
  fall back to the default face and shadow the rest).
* **filter's colour functions.** brightness / contrast / grayscale / sepia
  / saturate / invert / opacity compose into one affine sRGB transform in
  PaintState, applied to vertex colours and generated texels;
  `drop-shadow()` paints as an outer shadow of the border box.

**Where it stands: samples 12/35 + 11, hand 46/47, harvest 178/210 + 18
(layout untouched); 8,334 checks; hosts 23/23. Emoji render in colour on
15 samples; stats reads like Chrome.** Open: `backdrop-filter` (10 samples —
needs the host to sample the back buffer under a shader, i.e. a canvas
item split around each glass panel), `mask-image` fades, an opacity group
layer, `url()` images / `border-image`, `@container`, template/slot.

### Reference bug, now fixed: `<br>` was dropped in a shrink-to-fit box

FIXED (see the `IsForcedBreak` / `ForcedBreakBox` commits). Kept here because
the shape of it is worth remembering: a shrink-to-fit box lays its content out
TWICE, and the second pass walks the wreckage of the first.

The C# engine lost a forced line break inside any box that lays its inline
content out twice: an `inline-block` or an absolutely positioned box with auto
width. Floats and plain blocks are fine because they only lay out once.

```html
<div style="display:inline-block">a<br>b</div>
```

renders as ONE line, and the box does not shrink to fit either (it stays at
the full container width). weva-landing's `<h1>` loses its break this way,
which is the last structural difference on that page — Chrome and the port
both report 137 boxes including the `br`, the reference reports 136.

The mechanism, from instrumenting `CollectInlineInner`:

* Pass 1 walks the container's children and sees
  `TextRun InlineBox<span> TextRun InlineBox<br> TextRun`. The `<br>` branch
  emits a synthetic `"\n"` item with `WhiteSpace = "pre"`, the breaker fires
  its forced break, and `AttachInlineFragmentsToLines` re-parents the `<br>`
  InlineBox onto a LineBox.
* Pass 2 walks the SAME container and sees
  `TextRun InlineBox<span> TextRun TextRun TextRun` — the `<br>`'s InlineBox
  is gone from the child list, replaced by the plain TextRun the breaker
  emitted for it. No `<br>` branch runs, so no forced break, and the box is
  left with a single line.

A `<span>` survives this round trip because its text fragments carry its
Element and `AttachInlineFragmentsToLines` matches on that; a `<br>` has no
fragments of its own, so it only ever gets the empty-inline fallback.

Forcing `WhiteSpace = "pre"` on a re-collected single-newline run does NOT
work — the replacement run is EMPTY, not a bare `"\n"`, because the newline
was consumed by the break it caused. Nothing about its text or style says it
was ever a break.

The fix is a marker that rides along with it: `TextRun.IsForcedBreak` plus
`TextRun.ForcedBreakBox` (the originating InlineBox), set on the
`LineBreaker.Item` for the `<br>` and stamped onto the run the breaker emits.
A second collection pass sees the marker, re-emits the synthetic newline item
so the break happens again, and re-registers the box so it lands back in the
tree with a rect. `RentItem` hands back pooled Items without clearing them, so
`MakeItem` stamps both fields on every path.

Two things this exposed while fixing it:

* With the break restored the atom still would not shrink — 1280px wide for
  36px of text. For a BLOCK child the atom's max-content was read from that
  child's laid-out `Width`, which `LayoutBlock` had just set to the atom's
  full available width. Only inline content took a real max-content path.
* A `<span>` survives this round trip where a `<br>` did not, because
  `AttachInlineFragmentsToLines` matches fragments by Element and a span's
  text runs carry its Element. A `<br>` has no fragments of its own, so it
  only ever got the empty-inline fallback.

weva-landing's element count now matches Chrome exactly (137 = 137).

### Reference bug, now fixed: a `<span>` inside an inline-block reported zero width

FIXED (commit "an inline box inside a shrink-to-fit atom keeps its rect").
Pre-existing, verified by running the repro at the session's starting commit.
EVERY inline box inside a shrink-to-fit atom ended up with a zero-width rect:

```html
<div style="display:inline-block"><span>hello</span></div>
```

The atom is sized correctly (36px) but the span's box is `w = 0`. The same
span in a plain block is correct. Paint therefore cannot draw the span's
background, border or decorations, and hit-testing has nothing to hit — so
any styled inline inside a badge, pill or chip built from an inline-block is
affected, not just the layout dump.

Instrumenting `AttachInlineFragmentsToLines` shows the cause is a DOUBLE
attach. The same span object is attached twice:

```
DBGSPAN attach span#31291646 container=div                 <- correct, inside the atom
DBGSPAN attach span#31291646 container=AnonymousBlockBox   <- wrong, outer level
```

The first attach finds the span's text fragments on the atom's own line and
computes a correct bbox. The second runs at the OUTER container, whose lines
hold only the atoms themselves (`LINE(BlockBox TextRun<el=body> BlockBox …)`)
— no fragment there carries the span's Element, so it falls into the
empty-inline fallback, which sets `Width = 0` and overwrites the good rect.

The leak is real: `LayoutInline`'s empty-container early return pops
`pendingOofBoxes` but not `pendingInlineBoxes`, so those boxes reach the
caller's slice and the caller attaches them to its own lines.

**But closing that leak is the WRONG fix, and the C# suite does not catch
it.** Attaching those boxes at the empty container made every repro pass and
kept all 9,906 tests green — while breaking **18 harvest cases (184 → 166)**.
Some callers legitimately rely on inline boxes propagating up to the parent
context. Only the corpora caught it; it was reverted.

The actual cause is one level up. Pass 1's `AttachInlineFragmentsToLines`
calls `ClearChildren()` on every span it places, and the atom's
snapshot/restore only covered its TOP-LEVEL child list — so pass 2 received
the spans as empty shells, found no items, and fell into the empty-container
branch in the first place. The fix captures each descendant InlineBox's own
children in the snapshot and restores them too, so pass 2 never reaches that
branch. Repro: `sp.html` / `sf.html` in the scratch mini corpus.

Worth remembering as a rule: **for anything touching the two-pass layout
paths, the corpora are the regression gate, not the unit suite.**

### Open reference bug: a flex item's max-content measured before flex restructures it

Diagnosed, not fixed. Sibling of the stale-height bug below, and what remains
of weva-landing after that one was fixed (147 → 80 differences).

```html
<div class="nav"><div class="brand"><span class="lg"></span> Weva</div>…</div>
.nav   { display: flex }
.brand { display: flex; gap: 11px }
.lg    { width: 26px; height: 26px }
```

`.brand` measures **82** in the reference; Chrome and the port both say **73**
(26 logo + 11 gap + 36 text). Delete the space before `Weva` and all three
agree — so the 9px is exactly that space at 20px.

The space is legitimate in the PRE-flex layout: before flex runs, `.brand`'s
children are inline, so the logo span and the text sit on one line and the
space between them is real. Flex then blockifies them into separate items,
which makes that space LEADING whitespace of the anonymous text item — and
leading whitespace collapses away (the line breaker's `AppendCollapsing`
already skips a space token when nothing is on the line yet). The reference's
max-content is taken from the pre-flex inline measurement and never re-derived
from the post-flex item structure, so it keeps a space that no longer exists.

Same shape as the stale-height bug: **a measurement taken before flex layout
has restructured the children.** Repro: `lead2.html` in the scratch corpus.

### Reference bug, now fixed: a line sized from an inline-flex atom's STALE height

FIXED (commit "size a line from an inline-flex atom's flex height"). An
`inline-flex` atom that contains an inline-block was measured at its
block-STACKED height when the surrounding line is built, then
corrected to its real flex height afterwards — and the line keeps the stale,
taller value. Minimal case (`nest.html` in the scratch corpus):

```html
<div class="w"><span class="eb"><span class="dot"></span>Text</span></div>
.eb  { display: inline-flex; align-items: center; padding: 6px 13px; border: 1px solid; font-size: 13px }
.dot { display: inline-block; width: 8px; height: 8px }
```

`.w` comes out **36.86** tall in the reference. Chrome says **28.84** and the
port says 28.86, so the port is right and the reference is 8px — exactly the
dot's height — too tall. Remove the dot and both engines agree, so it is the
inline-block INSIDE the inline-flex that triggers it.

Instrumenting `LineBreaker.AppendAtom` shows the atom's height at line-build
time:

```
DBGATOM span.eb h=36.859 a=29.859 d=7     <- with the dot: block-stacked (8 + 14.86 text + 14 frame)
DBGATOM span.eb h=28.859 a=21.859 d=7     <- without it: correct flex height
```

36.859 is what you get by STACKING the dot and the text instead of laying
them out in a row — i.e. the flex layout has not run yet (or ran before the
anonymous flex item wrapping the raw text existed). The final tree reports
`.eb` at 28.86, so the atom is fixed later; only the line box keeps the old
number.

This put weva-landing 8px out on everything below its hero (`.eyebrow` is an
inline-flex holding an 8px `.pulse` dot), which was 147 of its differences;
it is now 80.

The fix takes the cross extent from `PositioningPass.FlexIntrinsicCross` —
the same non-destructive helper the flex code already uses for intrinsic
sizing — applied AFTER all of the atom's layout, just before the line item is
built. Two approaches that do NOT work, recorded so they are not retried:

* Wiring `FlexLayout` into `InlineLayout` and calling `Layout(atom)`. It fixes
  the minimal case and breaks the next one up (an `<h1>` landed at y=4 instead
  of 48.86) — `FlexLayout` shares `LayoutScratch` with the inline pass, so
  re-entering it corrupts the line being built.
* Correcting the height right after `LayoutBlock`. The `RelayoutContentAt` at
  the end of the shrink-to-fit path overwrites it.

### Twelfth pass: sharpening the instrument. 17/35 + 11

The gate moved 15 -> 17 genuinely agreeing (28/35 counting Chrome-arbitrated
reference bugs), and the biggest single lesson was about the harness rather
than either engine.

**The oracle was measuring its own rounding.** Both dumps printed 2 decimals
and the oracle compared the strings exactly. Two independent implementations
accumulate the same arithmetic in slightly different orders, so a value that
lands on a decimal midpoint prints differently on the two sides for reasons
that have nothing to do with layout — and the oracle then reported a 0.01
difference and every box below it. weva-landing showed 80 differences that
were ALL one such tie at `.stats` cascading downward, and the cascade buried a
real 56px difference further down the page.

Both dumps now print four decimals, and a one-last-place gap is read as
agreement (`same_value` in run_oracle.py). That is two orders of magnitude
TIGHTER than the exact 2dp comparison it replaces, not looser: the noise floor
went from 0.01 to 0.0001. Measured across the corpora it is strictly better —
hand and harvest identical, samples 15 -> 16 agreeing before any engine fix,
because one page had only ever been a rounding artifact. When an oracle
reports differences you cannot explain, check its resolution before chasing
the differences.

**Port bugs fixed**

* **Preserved newlines never broke a line.** CSS Text L3 §4.1.1 makes a
  newline kept by `pre`/`pre-wrap`/`pre-line`/`break-spaces` a segment break,
  and a preserved segment break forces a line break. The preserved-whitespace
  path placed a whole text node as ONE unbreakable fragment, so a nine-line
  `<pre>` listing laid out as a single 1.3kpx-wide line and reported one
  line-height of height. `preserve_newlines` is a third axis alongside
  `collapse_whitespace` and `allow_wrap` — `pre-line` collapses spaces like
  `normal` while still breaking at every newline — and an empty segment still
  pushes a zero-width fragment, because a blank line in a `pre` is a line and
  `flush_line` drops a fragment-less one.
* **The `<button>` centering pass ran on flex buttons.** Its guard was a
  comment ("a button with an author `display: flex/grid` is laid out
  elsewhere") rather than a condition; `finish_height` runs for those too, so a
  column flex button with `justify-content: center` was centred twice. Now
  conditioned on the button actually establishing a flow formatting context.

**Reference (C#) bugs fixed — all three confirmed against Chrome**

* **A list item's marker took the li's own style.** `ComputeMarker` returned
  null whenever the page had no authored `::marker` rule, and BoxBuilder's
  fallback for a null marker style is the `<li>`'s OWN ComputedStyle — handing
  the anonymous marker box the li's padding, border, margin and background.
  `li { padding: 5px 10px }` measured 26.9 where Chrome says 26. `::before` and
  `::after` legitimately return null (they need `content` to generate a box at
  all) but a list item HAS a marker whether or not anyone styled it, so
  `ComputePseudoElement` grew an `alwaysProduce` flag. Note BOTH harnesses hid
  this: neither BaselineGen's LayoutDump nor LayoutTestHelpers wired
  `MarkerStyleOf` the way UIDocumentBuilder does at runtime. **A harness that
  omits a runtime hook does not just miss bugs, it manufactures them.**
* **An atomic inline dropped its vertical margins from the line.** CSS 2.1
  §10.8.1 is explicit that an inline-block contributes its MARGIN box;
  LineBreaker read `AtomBox.Height`, the border box. `input { margin-bottom:
  6px }` sat in a line box exactly 28 tall. Captured Chrome directly on the
  isolated case to settle it: Chrome 125.28, port 125.311, reference 119.311.
* Together these took audit-validation from 71 differences to **2**.

**On arbitration.** Both C# bugs above were cumulative: each shifted everything
below it down the page, and since Chrome is compared on ABSOLUTE geometry, a
single early offset makes Chrome side with nobody for the rest of the document.
That is why audit-validation reported "chrome agrees with neither" 67 times for
what turned out to be two bugs. When a page shows a long run of three-way
disagreement, look for one shared offset at the top of the run rather than a
page full of independent problems.

**Still open on the seven failing samples**

* `audit-validation` (2): an `<input>` baseline-aligned beside a 90px
  `<textarea>` — ref 1861, cand 1845, Chrome 1892, so all three disagree.
* `card-component` (4): `<template>`/`<slot>`, which the port does not build.
* `combat-hud` (1), `dialogue` (1), `randhtml` (1): sub-pixel text-measurement
  divergences where Chrome sides with neither engine.
* `inventory` (41): Chrome's 1fr re-growth from an aspect-ratio transferred
  minimum.
* `menu` (47): `@container`, which BaselineGen's single-pass never applies.

### Thirteenth pass: two engines, one card. 17/35 + 12

Chasing ONE number — menu.html's `.card` at 108.57 against the reference's
113.15 — turned up four bugs, two on each side.

**Port: `@layer` was never implemented.** Layered rules kept the unlayered
ordinal, so they competed on specificity alone and a layered rule beat the
unlayered one it was written to lose to. `compare_declarations` already had the
layer axis right in both directions; it was simply never given an ordinal.
Both forms now work, including the statement form `@layer a, b, c;` — whose
whole purpose is fixing the order, since a layer's priority comes from where it
is first NAMED, not where its rules sit.

**Port: no line box had a strut.** CSS 2.1 §10.8 puts a zero-width inline box
with the containing block's font on every line. Without it a line holding only
an inline-block came out exactly the atom's height.

**Reference: an atomic inline dropped its vertical margins** from the line
(§10.8.1 says margin box; LineBreaker read the border box).

**Reference: `anchor-name` matched text runs.** A TextRun carries its element's
ComputedStyle, so an anchor WITH TEXT registered its run too, and the run comes
later in pre-order, so it won. Every `anchor()` then resolved against the run's
geometry. Every existing anchor test used an empty anchor, which is exactly why
this survived a whole test file.

**Chrome nearly sent us the wrong way twice, and the lesson is the same both
times: check what the arbiter is actually measuring.**

* On `.card`, Chrome reported 113 — matching the reference's 113.15 — but its
  `h2` was 25 to our 20.57, because `font: bold 18px sans-serif` escaped the
  capture's synthetic face. The totals agreed by coincidence, out of different
  parts. A stripped-down repro with explicit font properties gave Chrome 44.56
  against the port's 40 and settled it the other way.
* Chrome's `<button>` UA font is `13.333px`, not inherited. Both engines
  inherit the page font, so a label that wraps here does not wrap there. That
  is a real shared divergence and still open.

**Where the samples stand.** Six still differ: `audit-validation` (2, an
`<input>` baseline-aligned beside a 90px `<textarea>`), `card-component` (4,
`<template>`/`<slot>`), `combat-hud`, `dialogue` and `randhtml` (1 each,
sub-pixel text measurement Chrome sides with neither on), and `inventory` (41,
Chrome's 1fr re-growth from an aspect-ratio transferred minimum). `menu` went
from 47 differences to passing; `stock-dashboard` from 314 to 5.

### Fourteenth pass: aspect-ratio relates content boxes. hand 47/47

CSS Sizing L4 §5 says the ratio relates the two dimensions of the box that
`box-sizing` selects — with the default content-box, CONTENT width to CONTENT
height. The reference divided the BORDER-box width and then added the vertical
frame, counting the frame twice: `width: 101px; border: 1px; aspect-ratio: 1/1`
came out 105 where Chrome and the port both say 103.

The derivation had been written out FOUR times — BlockLayout.FinalizeBlockSize,
LayoutEngine's aspect-ratio fixup, and both of FlexLayout's directional helpers
— and every copy carried the same bug, one with a comment admitting it ("v1
simplification ignores box-sizing for ratio derivation"). They now share
`AspectRatioMath`, which also fixed the height-from-width direction no repro
had reached yet. This is the second time in this port that a rule copied into
several places was wrong in all of them; the first was the two box builders.

**hand is now 47/47 with zero reference bugs** — this was the last one.
inventory went from 41 differences to 13, and the goldens' card grid now
measures 240x240 against Chrome's own 240x240 (its baseline PNG had been
rendered at the old 242 and was regenerated; the other 37 were reverted).

**Open, with the analysis done.** Both engines mis-place a flex item centred in
a container whose height came from `aspect-ratio`, and they do it in DIFFERENT
configurations, so the oracle sees a difference where both are wrong:

* Minimal case (`width: 200px; aspect-ratio: 2/1; border: 1px; display: flex;
  align-items: center` around a 32px glyph): Chrome and the port put the glyph
  at 35, the reference at 35.998 — and at 155.998 against 151 when the border
  is 5px, so the reference is adding the border a second time. It looks like
  the cross-axis centre is taken against the container's BORDER-box height
  while the offset is measured from its CONTENT top; `containerCrossSize` at
  FlexLayout.cs:223 reads `ContentHeight` correctly, so the culprit is one of
  the paths that overwrite it (the min-floor near :471 and the pre-pass-2 stamp
  near :505 are the candidates).
* inventory's `.slot` is the mirror: there the REFERENCE matches Chrome
  (197.96 against 197.95) and the PORT is 1px high, and that slot's height
  comes from `aspect-ratio` on a 1fr grid track rather than an explicit width.

Both need to be fixed against Chrome rather than against each other.

### Fifteenth pass: 34/35. What the last four turned out to be

Every one of the four samples left after the aspect-ratio work had been written
off in this document as "sub-pixel text measurement, Chrome sides with neither".
Three of them were ordinary spec bugs, and the pattern in all three was that a
number landed on a UNIT rather than drifting:

* **combat-hud.** `.buff-time` holding "12s" came out exactly 18 wide in a
  36px circle at `left: 50%` — exactly the available space. Its two siblings
  ("8s", "4s") fit in 18 and agreed, so only one of three identical elements
  differed, which is what marks a clamp rather than a measurement. CSS 2.1
  §10.3.7's shrink-to-fit is `min(preferred, max(preferred minimum, available))`
  and the `max` is the point: a box may not be squeezed below what its content
  needs. The formula was right and an unconditional clamp after it undid the
  `max`.
* **audit-validation.** Chrome's UA scrolls a `textarea`, which by §10.8.1 pins
  its baseline to the bottom MARGIN edge instead of its last line box. Neither
  UA sheet had `overflow: auto`, so the baseline moved with the wrapped content
  and dragged the inputs beside it off by a line. Bisecting the section found
  it: removing the textarea took the difference to zero, its attributes changed
  nothing, and emptying its text also took it to zero — multi-line CONTENT
  mattering is only consistent with a content-derived baseline.
* **dialogue.** `<strong>` sat 0.16 to the right, and at 16px with
  `letter-spacing: 0.01em` that is exactly ONE spacing. Letter-spacing sits
  BETWEEN characters, so a run of n characters carries n-1 — and a line break
  restarts the count, because the spacing that would follow a line's last
  character has nowhere to sit. The port carried the count across the break.

**Method note.** Twice the first synthetic repro showed the two engines
AGREEING, which would have read as "no bug" if taken at face value: the
textarea repro's content did not wrap, and the dialogue repro initially omitted
the page's `letter-spacing`. Extracting the real section with the real
stylesheet reproduced both. Prefer bisecting the actual page over rebuilding it
from memory.

**randhtml, the last one.** `.party` is a column flex whose height the
reference reports as 107.706 while its own placed children span 108.026 — the
container does not contain its children, which is internally inconsistent
whatever Chrome says (107.94, between the two, and unable to arbitrate because
its letter-spacing convention differs from both engines). The port's value is
the self-consistent one. The shortfall is 0.32 across two children, i.e. 0.16
each, which is one letter-spacing unit at this page's size — the same quantity
dialogue turned on, so the reference's flex main-size sum is likely measuring
its items through a path that counts spacings the way the port used to.

### Sixteenth pass: the render side needs a different mirror than Chrome

With the layout gate at 35/35 the remaining half of "the samples work in Godot"
is rendering, and the first attempt to measure it was wrong twice over. Both
mistakes are worth keeping, because neither failed loudly.

**Chrome cannot referee the rasteriser.** Captured with `--metrics=mono` — the
setting the layout capture needs — its screenshots draw the synthetic face,
whose glyphs are solid BOXES; the diff is then text everywhere and says nothing
about rendering. Captured without it, Chrome uses its own faces and its own UA
sheet, and the diff is the font mismatch instead. There is no setting that
matches both metrics and glyph shapes, so a pixel comparison against the
browser is not the render gate.

**The right mirror is the port's own software backend.** `tools/weva_render`
and the Godot host consume the IDENTICAL draw list from the same build, so a
difference is a difference between the backends with cascade, layout and
tessellation held fixed — which is what ARCHITECTURE.md §1 asks for.
`hosts/godot/compare_render.py` already did this for one document;
`hosts/godot/compare_all.sh` now runs it over a corpus.

Two flags that silently produce nonsense, both now documented where they are
used:

* `--headless` DISABLES rendering. Every capture exits 0 and writes no file, so
  35 samples "failed" in a way that looks like a Godot problem rather than a
  flag.
* `--engine-font` puts Godot on the engine's real face while weva_render stays
  on the core's stub. Every glyph then disagrees and the backend comparison is
  swamped. For a backend check both sides must use the stub.

**First real finding.** With fonts matched, quests disagrees on 27% of ink —
software draws 616,561 ink pixels, Godot 401,140. It is not a vertical offset
(the best shift is dy=0) and it is not the font; the two render the same text
in the same places, and the missing coverage is in the card fills. That is the
next thing to chase.

Also worth noting: compare_render reports FAIL "the two images do not even
share a page colour" for `#181228` vs `#181229`, a one-unit sRGB rounding
difference. That verdict line is too strict to be useful and should compare the
page colour with the same tolerance it applies to everything else.

**The backend baseline.** Two earlier tables here were measured wrong and the
reason is the reusable part: "ink" meant pixels differing from the page behind
them, and no choice of page colour survives these documents. Each image's own
modal pixel breaks on a gradient (inventory read 58.8% when the images differ
almost nowhere); one shared modal pixel moves when the RENDERER changes (the
box-shadow fix shifted it and inventory went 3.4% -> 9.6% while its
over-tolerance stayed at exactly 12.66%, so not one pixel had changed); the
corner pixel is stable but calls most of a gradient page ink (vendor read 18%
while looking identical).

The difference image answers the question directly, with no page colour at all:
an edge pixel differs a LITTLE, a shape drawn wrongly differs a LOT. The report
carries both counts and gates on the second.

**31 of 35 samples pass the 2% structural gate.** The four that do not:

| sample | structural | over-tol |
|---|---|---|
| glass | 39.9% | 87.7% |
| quests | 16.9% | 82.7% |
| match3 | 7.0% | 76.7% |
| neon | 3.9% | 82.6% |

And a second group that passes structurally while differing a little almost
everywhere — flex-playground (0.00% / 43.6%), hud (0.08% / 69.4%), stats
(0.01% / 31.8%), leaderboard (0.00% / 30.1%), grid-playground (0.01% / 24.6%).
Nothing is in the wrong place on those; the whole composite is a shade off,
which points at gamma or a blend mode rather than geometry. All four hard
failures also sit above 76% over-tolerance, so they may well share that cause
on top of their own.

### Seventeenth pass: the failing side of the render gate is the SOFTWARE one

The backend gate compares weva_render against the Godot host, and it is easy to
read a failure as a host bug. On these samples it is mostly the reverse.

**paint.cpp linearises every colour into the draw list.** The Godot host then
converts each vertex back with `linear_to_srgb()` before handing it over, so
Godot interpolates and blends in sRGB — which is where CSS defines compositing
and gradient interpolation, and what Chrome does. weva_render keeps the linear
values, interpolates in linear and converts once at the end, so every gradient
and every translucent overlay lands on different midtones.

Measured against Chrome at 140 sampled points per page (mean per-channel
difference, coarse but the contrast is not subtle):

| sample | software vs Chrome | godot vs Chrome |
|---|---|---|
| neon | 41.6 | **1.2** |
| hud | 18.0 | **2.1** |
| match3 | 33.2 | **6.4** |
| glass | 44.2 | 23.3 |
| quests | 55.6 | 49.0 |

So the host is already close to Chrome on most of the corpus, and the "differs
a little almost everywhere" group — hud at 0.08% structural against 69.4%
over-tolerance, and its four siblings — is weva_render's colour space, not
anything the host does wrong. The transfer curve says the same: sampling hud by
luminance, the two agree at the bright end (167->166.8, 184->184.1, 229->229.1)
and diverge through the dark and middle (39->24.2, 87->48.7), which is what
blending in the wrong space looks like.

**Two things are genuinely open on the Godot side**, and only two:

* `glass` (23.3 against Chrome) — backdrop-filter, which the host does not
  implement. Known, and it needs BackBufferCopy plus a screen-texture shader.
* `quests` (49.0) — BOTH backends are far from Chrome, so this is upstream of
  the rasteriser. Diagnosed: it is the outer `box-shadow` on `.log`, still
  darkening the panel it sits under even after the border-box knockout.
  Deleting that one declaration makes the Godot render match Chrome almost
  exactly — panel margin (232,231,233) against (232,232,234), card interior
  (35,30,52) against (35,30,52) EXACTLY, right margin (162,160,168) against
  (163,161,168) — against a baseline that reads (122,121,123) at the panel
  margin.

  Two things were ruled out on the way. The composite itself is right: that
  gradient (`linear-gradient(135deg, rgba(255,255,255,0.12),
  rgba(13,8,28,0.46))`) over a white canvas renders (199,198,203) against
  Chrome's (199,198,202) in isolation. And `backdrop-filter` is not the cause —
  removing it makes the panel DARKER, not lighter, so the port's partial
  implementation is currently compensating for this bug.

  The knockout arithmetic looks right for this shape on paper: the declaration
  is `0 34px 90px rgba(0,0,0,0.55), 0 10px 28px rgba(0,0,0,0.34), inset 0 1px 0
  rgba(255,255,255,0.16)`, and for a layer whose grow is smaller than the 34px
  offset the un-knocked band sits ABOVE the shadow rect and so is not drawn
  anyway. Something else in that path is covering the interior — the inset
  layer, or the multi-shadow loop, are the two candidates not yet eliminated.
  The reproduction is exact and cheap: render quests with and without the one
  declaration and sample (80,400).

Fixing weva_render's colour space is the larger job — its framebuffer is linear
end to end and its render tests carry expected values — so it is worth doing
deliberately rather than as a side effect of chasing a sample.

### Eighteenth pass: two wrong conclusions, and the shadow knockout reverted

This pass produced no fix and two retractions, both from the same root cause.

**The knockout is reverted.** CSS Backgrounds L3 §7.1 is right that an outer
shadow is drawn outside the border edge only, but the ring geometry was wrong.
Measured against Chrome on a plain `0 0 40px` shadow it removed most of the
falloff — white at 15, 25 and 35px out where Chrome has 249, 240 and 222 — and
dropped a blur-less spread shadow entirely (255 against 115). Filling the rect
is the lesser error: invisible under an opaque background, which is most of
them, where the ring lost shadows outright. The interior bleed it was fixing is
still real (vendor's cards are `rgba(..., 0.92)` and Chrome renders their
interiors flat) and still wants a ring that keeps the falloff.

**"vendor 29% -> 1.08%" measured the wrong thing.** That was the two BACKENDS
agreeing, and they agreed because both had lost the same shadow. Neither was
checked against Chrome.

**"the host double-darkens shadow rings" was a stale binary.** The Godot host's
`.so` had not been rebuilt since before the knockout, so it ran the old paint
code while weva_render ran the new one — which is exactly the shape of a real
backend bug, and read as one for a whole pass. Rebuilt, the two agree on 0.00%
of pixels; the host does nothing wrong here.

**Three stale-build traps in one session, all the same shape.** `dotnet run
--project Tools/TestVerifyAll` rebuilds the package sources but not BaselineGen,
so the corpora ran against an old binary. `ninja -C ~/weva/build-gcc` rebuilds
weva_render but not `~/weva/build-godot`, so the host ran old paint code. Each
time the stale artefact produced a plausible, wrong story that survived a full
pass. **A comparison between two artefacts is worthless unless both were built
from the tree under test** — rebuild every consumer before believing a
comparison, and treat "one side changed and the other did not" as a build
question first.

**Still open on shadows**, both verified against Chrome:

* the interior bleed under a translucent background (needs a correct ring);
* a blur-less spread shadow (`0 0 0 20px`) draws NOTHING in either backend,
  where Chrome draws a hard ring — `blurred_coverage(0, 0)` returning zero
  makes the first layer fail the `target <= accumulated` test.

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

## Phase 3 — the Unity host on the core: what the tooling still needs from the C ABI

Written 2026-09-11 after Phase 2 of the shared-core plan (the Unity package
hosts the core through `Packages/com.wevaui/Runtime/Native`; receipt in
`docs/verification/unity-host-prototype.json`). The editor tooling
(`Packages/com.wevaui/Editor/**`, `Runtime/DevTools`, `Runtime/HotReload`,
`Runtime/Designer`, `Runtime/InPlace`) was mapped tool by tool against
`weva_c.h`. What needs no engine at all: the whole Designer IR (20 files that
emit HTML/CSS strings), `InlineStyleEdit` (source-text splicing that
`weva_element_set_style` already mirrors for the live document), the file
watchers, the preview toolbar/viewport types, the setup and importer utilities.
What is covered today: a document from HTML/CSS with viewport, update, draws
and input; element lookup by selector, tag, attributes, text, border-box bounds,
computed style by name, inline style, mutation (`set_html`, `append_html`,
`remove`, `set_attribute`, `set_text`), hit testing, events with handler
names, `@font-face` listing, the layout dump.

Missing, ranked by how many tools need it (decision: expose through the ABI
unless noted):

1. **Tree navigation** — `weva_element_parent`, `weva_element_children` /
   `child_count` / `child_at`, node kind (element vs text). Seven tools; the
   Designer's ancestor walk to `data-nid` (five call sites) has no substitute
   short of `query_all` + `weva_element_contains` per pointer move.
2. **Matched rules per element** — selector text, specificity, origin, layer,
   source order, `!important`, inline flag, and the losing declarations. The
   defining feature of the Elements panel's Styles pane; nothing in the ABI
   today. A structured blob with the `font_faces` buffer convention would do.
3. **Full box model** — margin/border/padding/content edges, not only the
   border box (`weva_element_box_model`). Six tools; approximable by a dozen
   computed-style string reads per element.
4. **Computed-style enumeration** — every property an element resolved,
   custom properties included; `weva_element_computed_style` is name-keyed.
5. **Change notification** — a mutation signal and the per-element
   invalidation set (Layout/Style/Paint). Draw, interaction, transient and form
   versions exist, none per element.
6. **Box-tree enumeration** including anonymous, line and text boxes; the
   layout dump is the only path and it is a JSON string.
7. **Engine counters** — cascade and paint cache hits, per-stage timings,
   box and element counts (the DevTools stats window is a no-op without them).
8. **Source positions** — neither engine has them; if jump-to-source is
   wanted the core must add it (nothing to match).
9. **A devtools hit test** that ignores `pointer-events` and `visibility`.
10. Capture-phase event interception (one tool; `element_at` on the pointer
    is the substitute) — keep host-side.
11. Per-stylesheet replacement with origin and ordering (hot reload) —
    `set_css` wholesale is a viable substitute; keep host-side.
12. Identity-preserving HTML reload (the C# `DomDiffer`) — best done inside
    `weva_document_load_html`, as `set_css` already promises for styles.
13. Overlay paint injection — a native host draws after `weva_document_draws`;
    drop.
14. Referenced-asset list, a font-resolution miss log, HTML parse diagnostics
    with positions — three small tools; `missing_assets`, `font_faces` and
    `css_diagnostics` cover adjacent slices.
15. Viewport readback and a `prefers-color-scheme` knob in `weva_config`.

The C# goldens through the core (`hosts/unity/goldens_from_unity.py`):
layout against Chrome 43/47 (24: monospace not registered in the run and the
ellipsis line's height; 25: `dialog[open]` with author `top`/`left` laid out
centred as if modal; 26: `line-height: normal` 24.2 vs Blink's rounded 23;
42: `content-visibility: hidden` children sized 30 vs Chrome's 100 with two not
dumped). Paint against the C# baselines 25/38: the baseline renderer draws text
as word bars, so text-bearing pages differ by construction, and 25's
`::backdrop` is not painted by the core.

## Phase 3 — feature inventory: the C# engine against the core (2026-09-11)

Method: every section of `Packages/com.wevaui/CSS_FEATURES.md` was checked
against the core's sources by consuming call site, not by the property
registry (`src/generated/css_properties.inc` is generated from the C#
`CssProperties.cs`, so every C# property is registered whether or not anything
reads it). Decisions: port (into the core), expose (already in the core, needs
an ABI surface), keep host-side (C#), drop.

**Port to the core, in the order the samples and tests lean on them:**

1. `mask` / `mask-image` family and `mix-blend-mode`: nine `mask-*` properties
   and both blend properties are registered with no consumer; `BlendMode` and
   `composite_layers` exist in `render_interface.h` with no caller.
2. Colour. DONE 2026-09-11, core ahead (the C# parses rgb/hsl/hwb only):
   `lab()`, `lch()`, `oklab()`, `oklch()`, `color()` over the predefined
   spaces and XYZ, the modern space-separated syntax with `/ alpha` and
   `none` on every colour function, and `color-mix()` in each of those
   spaces with the four hue methods (`color_space.cpp`). Out-of-gamut
   colours clip rather than gamut-map. `light-dark()` DONE 2026-09-11: the
   cascade rewrites it after var() substitution, the element's inherited
   `color-scheme` wins over the host's preference, and ABI minor 28 adds
   `weva_document_set_color_scheme` (Godot `dark_color_scheme`, Unity
   `NativeDocument.SetColorScheme`).
3. `@import` DONE 2026-09-11 (`at_import.cpp`: fetched through the asset
   reader relative to the base path, spliced under its media / supports /
   layer conditions, nested with a cycle guard; an unreadable sheet is
   listed by `weva_document_css_diagnostics`). `@scope` still lands in the
   unsupported-at-rule list (`cascade.cpp:828`).
4. `::placeholder` and `::selection`. DONE 2026-09-11: both are computed for
   text fields (`input`, `textarea`) in the style cache and read at paint;
   `::placeholder` honours `color` and `opacity`, `::selection` its
   `background-color` for the band. Not done, as in the C#: a
   `::placeholder` font and the `::selection` glyph colour.
5. Text: `text-align: justify`, `text-align-last` and `text-justify` DONE
   2026-09-11 (`justify_fragments` in `inline_layout.cpp`: inter-word and
   inter-character, the last line and a forced-break line take
   text-align-last, intrinsic widths take the spread back off). `hyphens:
   manual` DONE 2026-09-11 (soft hyphens invisible, a break there draws a
   hyphen; `auto` behaves as `manual`, no dictionary in either engine).
   Still open:
   `font-variation-settings` / `font-optical-sizing` / `font-feature-settings`
   (no font-backend path for axes or features; the `weva_font_backend` table
   would need them), `-webkit-text-stroke`.
6. `filter: hue-rotate()` (the one missing filter function, one matrix). DONE
   2026-09-11 (`paint.cpp`, test in `test_backdrop_filter.cpp`).
7. 3D transform functions. DONE 2026-09-11, core ahead: the C# treats them
   as identity too (its TransformFunctionTests pin that), which was the
   real state, not a projection. The core now projects orthographically:
   translate3d/scale3d keep x and y, rotateX/rotateY foreshorten by the
   cosine, rotate3d keeps the top-left 2x2 of the rotation, matrix3d its
   affine part; translateZ/scaleZ/perspective() are identity.
   Also the individual `translate` / `rotate` / `scale` properties (registered,
   unread).
8. Scroll snap DONE 2026-09-11 (`scroll_snap.cpp`, the 13 properties
   appended to the registry): a programmatic scroll snaps at once, a wheel
   scroll settles after 150 ms of quiet with a 250 ms ease-out, `always`
   stops, `proximity` within half the scrollport, padding/margin, start/
   end/center, both axes. Still open: `scroll-behavior: smooth` and
   snapping after a scrollbar-thumb drag.
9. Small drops: `caret-color` DONE 2026-09-11 (`paint.cpp` caret_color_of), `list-style-image`
   (`Box::list_marker_image` declared, never assigned), `image-rendering`
   (nearest only, `background.cpp:948`), `background-attachment`.
10. Component-scoped stylesheets (`components.h:7-12` defers the scope stamp;
    C# `Components/Scoping` has no counterpart).
11. `@supports selector(...)`, the one missing arm of `media.cpp:260-320`. DONE
    2026-09-11 (`media.cpp`, test in `test_cascade.cpp`).
12. Subgrid: C# gates on `display: subgrid`, the core on
    `grid-template-columns: subgrid` (the spec form); pick the core's and fix
    the C# samples.
13. `position: sticky` DONE 2026-09-11: `sticky.cpp` pins each sticky box
    against its scroll container's scrollport from the current scroll
    positions (top/bottom/left/right, held inside the containing block);
    paint, hit testing and element bounds add the offsets.
14. `min-content` / `max-content` / `fit-content` / `fit-content()` as `width`
    DONE 2026-09-11 on blocks, floats and inline-blocks (block layout routes
    the keyword through the shrink-to-fit probes; min/max-width clamp as
    usual). Still open: the keywords as `height`, `min-width`, `max-width`
    and `flex-basis`.
15. `direction: rtl` reordering / `unicode-bidi` (shared gap; the caret work is
    open on both sides).
16. View Transitions (`Runtime/ViewTransitions`): entirely absent; it needs
    two layout passes and a paint snapshot, all core-side. Port if the API is
    to survive, otherwise drop (it is a documented v1 stub in C#).

**Expose through the ABI (already in the core):** the extended selector set
(`:lang`, `:dir`, `:target`, `:valid`/`:invalid`, `:in-range`, `:default`,
`:autofill`, ...; `:link`/`:visited` need host state), `env()` values
(safe-area insets), logical sizing properties, multicol, `@property`,
`content-visibility` / `contain`, `scrollbar-width` / `scrollbar-color`,
`quotes`, counters, `list-style-position: outside`, line clamping, vertical
writing modes; `cursor` (registered, no consumer, and no ABI surface: the host
cannot learn which cursor to show); `prefers-color-scheme` / a media-context
setter; explicit dirty-marking (`MarkStyleDirty` / `MarkLayoutDirty`); a phase
timing readout for host profilers; the ABI event set is wider than the C#
`EventKind` (toggle, submit, reset, close, cancel, invalid, context menu,
composition, value-changed, scroll) and C# should adopt it.

**Keep host-side:** the Designer IR, in-place source splicing, hot-reload file
watching (the Godot host's live reload is the model; the core offers
`set_css` / `load_html` / `refresh_bindings`), the `[UIBind]` source generator
and reflection binding layer (the core takes a callback), DevTools windows
(over the ABI), the Unity input/clipboard/IME device bridges, the
`MonoBehaviour` shell of `WevaDocument`.

**Drop:** `::first-line` / `::first-letter`, `shape-outside`,
`position-try-fallbacks`, `display: ruby`, `empty-cells`, `@page`,
`@container` style/scroll-state queries and `cq*` units, the extra media
features (`prefers-contrast`, `color-gamut`, ...), `clip-path: path()` /
SVG sources, `font-variant: small-caps`, `text-justify`, `orphans` /
`widows`, `text-emphasis-*`, `font-synthesis-*`, `user-select`,
`scrollbar-gutter`, `overflow-clip-margin`, `resize`, `field-sizing`,
`box-decoration-break`, `backface-visibility`, `perspective-origin`,
`break-*`, the C# `Compiled` selector index (the core's shape-keyed match
cache does the same job; do not port two caches).

**Where the core is ahead (do not re-port):** `@layer` dotted hierarchies,
`@property`, `contain: size`, `content-visibility`, multicol, scrollbar
styling, `quotes`, counters, table cell `vertical-align`, outside list
markers, line clamping, logical properties, vertical writing modes, the
extended selectors, `env()`, `revert` / `revert-layer`, the animation
composition and cancellation rules verified against Chrome.

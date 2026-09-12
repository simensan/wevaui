# Unicode data

`libweva/src/grapheme_data.inc` contains generated Unicode 17.0.0 character
properties. `libweva/tests/data/GraphemeBreakTest-17.0.0.txt` is the official
Unicode grapheme segmentation conformance fixture. Both are distributed under
the [Unicode License V3](LICENSE.txt). The packaged addon includes this notice
and license because its native libraries embed the generated tables.

The generator, `tools/generate_graphemes.py`, verifies SHA-256 hashes before
using its inputs. Download these files from `https://www.unicode.org/Public/17.0.0/ucd/`
into one directory, then run `python tools/generate_graphemes.py /path/to/data`
from `godot-port`:

| File under the UCD directory | SHA-256 |
|---|---|
| `auxiliary/GraphemeBreakProperty.txt` | `d6b51d1d2ae5c33b451b7ed994b48f1f4dc62b2272a5831e7fd418514a6bae89` |
| `emoji/emoji-data.txt` | `2cb2bb9455cda83e8481541ecf5b6dfda66a3bb89efa3fa7c5297eccf607b72b` |
| `DerivedCoreProperties.txt` | `24c7fed1195c482faaefd5c1e7eb821c5ee1fb6de07ecdbaa64b56a99da22c08` |
| `extracted/DerivedCombiningClass.txt` | `191463abfbd202703c6fd6776a92a23ac44ec65e0476a7f95aa91ca492cef29b` |
| `PropList.txt` | `130dcddcaadaf071008bdfce1e7743e04fdfbc910886f017d9f9ac931d8c64dd` |
| `auxiliary/GraphemeBreakTest.txt` | `e2d134d2c52919bace503ebb6a551c1855fe1a1faec18478c78fff254a1793ec` |

The generated data has 964 non-default ranges; Hangul syllables are classified
arithmetically. There is no Unicode library or network dependency at runtime.
Segmentation follows [UAX #29 revision 47](https://www.unicode.org/reports/tr29/tr29-47.html).

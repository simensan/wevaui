// The Indic half of the shaper: Devanagari, Bengali, Gurmukhi, Gujarati,
// Oriya, Tamil, Telugu, Kannada and Malayalam, in the OpenType "2" shaping
// model (dev2, bng2, ...) the fonts a game ships use. What sets an Indic
// script apart from the Arabic path in UnityFontBackend.Shaping.cs is that
// glyph ORDER is not text order: a left matra (the i in "हि") is written
// before the consonant it follows, and a syllable-initial ra with a virama
// becomes a mark (the reph) drawn over the syllable's end. So a run is cut
// into syllables first, each syllable finds its base consonant, the font's
// basic features run gated to the glyphs they are for (rphf on the reph
// pair, half on a consonant before the base, blwf/pstf on one after it),
// the matra and the reph move, and the presentation features run over the
// result. The Unicode categories that drive it come from the core
// (weva_char_indic_category, ABI minor 42).
//
// This is the common-case shaper, not HarfBuzz's: no script-specific reph
// positions beyond "end of the syllable, before the vowel modifiers", no
// pre-base-reordering ra (Malayalam's), no Khmer or Myanmar. A syllable it
// does not understand shapes as the Arabic path would: in text order, with
// the font's features applied unconditionally.
using System.Collections.Generic;

namespace Weva.Native
{
    internal sealed unsafe partial class UnityFontBackend
    {
        // ---- categories, from the core ------------------------------------------

        private const int IndicOther = 0, IndicConsonant = 1, IndicVowelIndependent = 2, IndicMatra = 3, IndicNukta = 4, IndicVirama = 5,
                          IndicBindu = 6, IndicVisarga = 7, IndicAvagraha = 8, IndicConsonantDead = 9, IndicJoiner = 12, IndicNonJoiner = 13,
                          IndicConsonantMedial = 14, IndicConsonantSubjoined = 15, IndicSyllableModifier = 19, IndicTone = 20;
        private const int PosLeft = 1, PosTopAndLeft = 8, PosLeftAndRight = 7, PosTopAndLeftAndRight = 12, PosBottomAndLeft = 13, PosTopAndBottomAndLeft = 14;

        private readonly Dictionary<uint, (int Category, int Position)> _indicCategories = new Dictionary<uint, (int, int)>();

        private (int Category, int Position) IndicCategoryOf(uint cp)
        {
            if (!_indicCategories.TryGetValue(cp, out (int Category, int Position) c))
            {
                int position;
                int category = WevaNative.weva_char_indic_category(cp, &position);
                c = (category, position);
                _indicCategories[cp] = c;
            }
            return c;
        }

        // ISO 15924 (lower-cased) -> the OpenType script tags, the "2" model
        // first: the font's tag decides which model its lookups expect.
        private static readonly Dictionary<string, string> IndicNewTags = new Dictionary<string, string>
        {
            { "deva", "dev2" }, { "beng", "bng2" }, { "guru", "gur2" }, { "gujr", "gjr2" }, { "orya", "ory2" },
            { "taml", "tml2" }, { "telu", "tel2" }, { "knda", "knd2" }, { "mlym", "mlm2" },
        };

        private static bool IsIndicScript(string scriptTag) => scriptTag != null && IndicNewTags.ContainsKey(scriptTag);

        // The tag to look features up under: the "2" tag when the font has it.
        private static string IndicTableTag(GsubTable table, string scriptTag)
        {
            if (scriptTag != null && IndicNewTags.TryGetValue(scriptTag, out string newTag) && table.FeaturesByScript.ContainsKey(newTag)) return newTag;
            return scriptTag;
        }

        // The ra of each script: with a virama at a syllable's start it is the
        // reph. Tamil has no reph.
        private static readonly Dictionary<string, uint> IndicRa = new Dictionary<string, uint>
        {
            { "deva", 0x0930 }, { "beng", 0x09B0 }, { "guru", 0x0A30 }, { "gujr", 0x0AB0 }, { "orya", 0x0B30 },
            { "telu", 0x0C30 }, { "knda", 0x0CB0 }, { "mlym", 0x0D30 },
        };

        // ---- roles: which glyphs a basic feature is for ------------------------------

        private const int RoleReph = 1, RoleHalf = 2, RoleBelow = 4, RolePost = 8, RolePre = 16, RoleAll = 0;

        // The basic features, gated by role; then the presentation features,
        // ungated. In each set the lookups run in the font's lookup order.
        private static readonly (string Tag, int Mask)[] IndicBasicFeatures =
        {
            ("locl", RoleAll), ("ccmp", RoleAll), ("nukt", RoleAll), ("akhn", RoleAll), ("rphf", RoleReph), ("rkrf", RoleAll),
            ("pref", RolePre), ("blwf", RoleBelow), ("abvf", RoleAll), ("half", RoleHalf), ("pstf", RolePost), ("vatu", RoleAll), ("cjct", RoleAll),
        };
        private static readonly (string Tag, int Mask)[] IndicFinalFeatures =
        {
            ("init", RoleAll), ("pres", RoleAll), ("abvs", RoleAll), ("blws", RoleAll), ("psts", RoleAll), ("haln", RoleAll), ("calt", RoleAll), ("clig", RoleAll),
        };

        private struct Syllable
        {
            public int Start, End;   // logical positions, [Start, End), as segmented
            public uint StartCluster, EndCluster;   // the same as byte offsets, which substitutions do not move
            public int Base;         // the base consonant's position, or -1
            public bool Reph;        // starts with ra + virama and has another consonant
        }

        private readonly List<Syllable> _syllables = new List<Syllable>(16);

        // Whether the font gives a consonant a below-, post- or pre-base form
        // after a virama: whether the feature's lookups would change the pair
        // (virama, consonant) -- the "2" model's order -- or (consonant,
        // virama), tried on a scratch run the way HarfBuzz's would_substitute
        // does, since a font may spell the form as a ligature or as a
        // contextual rule (Nirmala UI's blwf is the latter). Asked once per
        // consonant and feature per face.
        private readonly Dictionary<(Source, string, uint), bool> _indicForms = new Dictionary<(Source, string, uint), bool>();
        private readonly List<RunGlyph> _savedRun = new List<RunGlyph>(64);

        private bool HasIndicForm(Source src, GsubTable table, string tableTag, string feature, uint consonant, uint virama)
        {
            var key = (src, feature, consonant);
            if (_indicForms.TryGetValue(key, out bool has)) return has;
            has = false;
            Dictionary<string, List<int>> byFeature = FeaturesFor(table, tableTag);
            if (byFeature != null && byFeature.TryGetValue(feature, out List<int> lookups) && virama != 0 && consonant != 0)
            {
                has = WouldSubstitute(src, table, lookups, virama, consonant) || WouldSubstitute(src, table, lookups, consonant, virama);
            }
            _indicForms[key] = has;
            return has;
        }

        // Runs the lookups over a two-glyph scratch run and reports whether
        // anything changed. The real run is put aside meanwhile.
        private bool WouldSubstitute(Source src, GsubTable table, List<int> lookups, uint first, uint second)
        {
            _savedRun.Clear();
            _savedRun.AddRange(_run);
            _run.Clear();
            uint slot = 0;
            for (int i = 0; i < _savedRun.Count; i++) if (ReferenceEquals(_savedRun[i].Src, src)) { slot = (uint)SlotOf(_savedRun[i].Id); break; }
            _run.Add(new RunGlyph { Src = src, Index = first, Id = (slot << SlotShift) | first, Base = -1, AttachedTo = -1 });
            _run.Add(new RunGlyph { Src = src, Index = second, Id = (slot << SlotShift) | second, Base = -1, AttachedTo = -1 });
            bool changed = false;
            foreach (int li in lookups)
            {
                for (int i = 0; i < _run.Count && !changed; i++)
                {
                    if (ApplyLookupAt(table, src, li, i, 0)) changed = true;
                }
                if (changed) break;
            }
            _run.Clear();
            _run.AddRange(_savedRun);
            return changed;
        }

        // ---- the pass ----------------------------------------------------------------

        // Shapes the run's glyphs of one face as Indic text: syllables, roles,
        // the basic features, the reordering, the presentation features.
        private void ApplyIndic(Face face, Source src, string scriptTag)
        {
            GsubTable table = GsubOf(src);
            if (!table.Present) return;
            string tableTag = IndicTableTag(table, scriptTag);
            _positioningScriptTag = tableTag;   // the marks (abvm, blwm) are under the same tag
            IndicRa.TryGetValue(scriptTag, out uint raCodepoint);
            uint viramaCodepoint = 0;

            // 1. Categories on every glyph, and the syllables.
            for (int i = 0; i < _run.Count; i++)
            {
                RunGlyph g = _run[i];
                (int category, int position) = IndicCategoryOf(g.Codepoint);
                g.Indic = category;
                g.IndicPos = position;
                g.Role = 0;
                if (category == IndicVirama && viramaCodepoint == 0) viramaCodepoint = g.Codepoint;
                _run[i] = g;
            }
            uint viramaGlyph = viramaCodepoint != 0 ? IndexOf(Lookup(face, viramaCodepoint)) : 0;
            Segment(src);

            // 2. Roles per syllable: the base, the reph pair, the halves
            // before the base, the below/post forms after it.
            for (int s = 0; s < _syllables.Count; s++)
            {
                Syllable syl = _syllables[s];
                var consonants = new List<int>();
                for (int i = syl.Start; i < syl.End; i++)
                {
                    int c = _run[i].Indic;
                    if (c == IndicConsonant || c == IndicConsonantDead) consonants.Add(i);
                }
                syl.Base = -1;
                syl.Reph = false;
                if (consonants.Count > 0)
                {
                    int first = consonants[0];
                    // A syllable-initial ra + virama followed by another
                    // consonant is the reph; it is not a base candidate.
                    if (raCodepoint != 0 && consonants.Count > 1 && _run[first].Codepoint == raCodepoint &&
                        first + 1 < syl.End && _run[first + 1].Indic == IndicVirama)
                    {
                        syl.Reph = true;
                        SetRole(first, RoleReph);
                        SetRole(first + 1, RoleReph);
                    }
                    // The base: the last consonant without a below/post form,
                    // scanning back; the reph's ra never; the first consonant
                    // when every later one has a form.
                    int baseAt = -1;
                    for (int k = consonants.Count - 1; k >= (syl.Reph ? 1 : 0); k--)
                    {
                        int at = consonants[k];
                        uint glyph = _run[at].Index;
                        bool formed = k > (syl.Reph ? 1 : 0) &&
                                      (HasIndicForm(src, table, tableTag, "blwf", glyph, viramaGlyph) || HasIndicForm(src, table, tableTag, "pstf", glyph, viramaGlyph) ||
                                       HasIndicForm(src, table, tableTag, "pref", glyph, viramaGlyph));
                        if (!formed) { baseAt = at; break; }
                    }
                    if (baseAt < 0) baseAt = consonants[syl.Reph ? 1 : 0];
                    syl.Base = baseAt;
                    // Consonants before the base take half forms with their
                    // virama; those after it their below/post/pre forms.
                    foreach (int at in consonants)
                    {
                        if (at == baseAt || (_run[at].Role & RoleReph) != 0) continue;
                        if (at < baseAt)
                        {
                            SetRole(at, RoleHalf);
                            if (at + 1 < syl.End && _run[at + 1].Indic == IndicVirama) SetRole(at + 1, RoleHalf);
                        }
                        else
                        {
                            uint glyph = _run[at].Index;
                            int role = HasIndicForm(src, table, tableTag, "blwf", glyph, viramaGlyph) ? RoleBelow
                                     : HasIndicForm(src, table, tableTag, "pstf", glyph, viramaGlyph) ? RolePost
                                     : HasIndicForm(src, table, tableTag, "pref", glyph, viramaGlyph) ? RolePre : 0;
                            if (role == 0) continue;
                            SetRole(at, role);
                            if (at - 1 >= syl.Start && _run[at - 1].Indic == IndicVirama) SetRole(at - 1, role);
                        }
                    }
                }
                _syllables[s] = syl;
            }

            // 3. The basic features, gated by role.
            ApplyFeatureSet(table, src, tableTag, IndicBasicFeatures, true);

            // 4. Reordering, syllable by syllable. Substitutions may have
            // merged glyphs, so the syllables are found again by cluster.
            ReorderSyllables(src);

            // 5. The presentation features over the result.
            ApplyFeatureSet(table, src, tableTag, IndicFinalFeatures, false);
        }

        private void SetRole(int at, int role)
        {
            RunGlyph g = _run[at];
            g.Role |= role;
            _run[at] = g;
        }

        // Cuts the run's glyphs into syllables on their categories:
        //   consonant (nukta)? (virama (joiner)? consonant (nukta)?)* virama?
        //   matra* (bindu | visarga | modifier | tone | avagraha)*
        // or an independent vowel with the same tail. Anything else is a
        // syllable of its own and is left as it is.
        private void Segment(Source src)
        {
            _syllables.Clear();
            int i = 0;
            while (i < _run.Count)
            {
                if (!ReferenceEquals(_run[i].Src, src) || _run[i].Id == 0)
                {
                    i++;
                    continue;
                }
                int start = i;
                int c = _run[i].Indic;
                if (c == IndicConsonant || c == IndicConsonantDead || c == IndicVowelIndependent)
                {
                    i++;
                    if (i < _run.Count && _run[i].Indic == IndicNukta) i++;
                    // Further consonants joined by a virama.
                    while (i + 1 < _run.Count && _run[i].Indic == IndicVirama)
                    {
                        int next = i + 1;
                        if (next < _run.Count && (_run[next].Indic == IndicJoiner || _run[next].Indic == IndicNonJoiner)) next++;
                        if (next < _run.Count && (_run[next].Indic == IndicConsonant || _run[next].Indic == IndicConsonantDead) && ReferenceEquals(_run[next].Src, src))
                        {
                            i = next + 1;
                            if (i < _run.Count && _run[i].Indic == IndicNukta) i++;
                        }
                        else break;
                    }
                    if (i < _run.Count && _run[i].Indic == IndicVirama) i++;   // a dead consonant at the end
                    while (i < _run.Count && _run[i].Indic == IndicMatra) i++;
                    while (i < _run.Count && (_run[i].Indic == IndicBindu || _run[i].Indic == IndicVisarga || _run[i].Indic == IndicSyllableModifier ||
                                              _run[i].Indic == IndicTone || _run[i].Indic == IndicAvagraha || _run[i].Indic == IndicConsonantMedial ||
                                              _run[i].Indic == IndicConsonantSubjoined)) i++;
                }
                else
                {
                    i++;
                }
                _syllables.Add(new Syllable
                {
                    Start = start, End = i, Base = -1,
                    StartCluster = _run[start].Cluster,
                    EndCluster = i < _run.Count ? _run[i].Cluster : uint.MaxValue,
                });
            }
        }

        // Every enabled lookup of a feature set once over the run in lookup
        // order; a role-gated lookup applies to a glyph carrying that role.
        private void ApplyFeatureSet(GsubTable table, Source src, string tableTag, (string Tag, int Mask)[] features, bool gated)
        {
            Dictionary<string, List<int>> byFeature = FeaturesFor(table, tableTag);
            if (byFeature == null) return;
            var masks = new SortedDictionary<int, int>();
            var all = new HashSet<int>();
            foreach ((string tag, int mask) in features)
            {
                if (!byFeature.TryGetValue(tag, out List<int> lookups)) continue;
                foreach (int index in lookups)
                {
                    if (mask == RoleAll) all.Add(index);
                    masks[index] = masks.TryGetValue(index, out int m) ? m | mask : mask;
                }
            }
            foreach (KeyValuePair<int, int> kv in masks)
            {
                int mask = all.Contains(kv.Key) ? 0 : kv.Value;
                for (int i = 0; i < _run.Count; i++)
                {
                    RunGlyph g = _run[i];
                    if (!ReferenceEquals(g.Src, src) || g.Id == 0) continue;
                    if (gated && mask != 0 && (g.Role & mask) == 0) continue;
                    ApplyLookupAt(table, src, kv.Key, i, 0);
                }
            }
        }

        // Within each syllable: a left matra moves to the syllable's start,
        // and the reph -- the glyph the rphf pair became, still carrying the
        // ra's cluster -- moves to the syllable's end, before its bindu,
        // visarga or modifiers, where the font's abvm anchors it over the
        // last letter. Syllables are re-found by cluster range, since the
        // basic features may have merged glyphs.
        private void ReorderSyllables(Source src)
        {
            foreach (Syllable syllable in _syllables)
            {
                if (syllable.Base < 0) continue;
                // The syllable's glyphs now: those whose cluster lies in the
                // syllable's byte range, recorded when it was segmented (the
                // substitutions since have moved positions, not clusters).
                uint firstCluster = syllable.StartCluster, endCluster = syllable.EndCluster;
                int start = -1, end = -1;
                for (int i = 0; i < _run.Count; i++)
                {
                    if (!ReferenceEquals(_run[i].Src, src)) continue;
                    if (_run[i].Cluster >= firstCluster && _run[i].Cluster < endCluster)
                    {
                        if (start < 0) start = i;
                        end = i + 1;
                    }
                }
                if (start < 0 || end - start < 2) continue;

                // Left matras first (a matra written on both sides of the
                // consonant has been split by ccmp into its halves).
                for (int i = start; i < end; i++)
                {
                    RunGlyph g = _run[i];
                    if (g.Indic != IndicMatra) continue;
                    int p = g.IndicPos;
                    bool left = p == PosLeft || p == PosTopAndLeft || p == PosLeftAndRight || p == PosTopAndLeftAndRight || p == PosBottomAndLeft || p == PosTopAndBottomAndLeft;
                    if (!left || i == start) continue;
                    _run.RemoveAt(i);
                    _run.Insert(start, g);
                }
                // Then the reph.
                if (syllable.Reph)
                {
                    int rephAt = -1;
                    for (int i = start; i < end; i++) if ((_run[i].Role & RoleReph) != 0) { rephAt = i; break; }
                    // The pair became one glyph: the ra's cluster survives.
                    if (rephAt >= 0 && !(rephAt + 1 < end && (_run[rephAt + 1].Role & RoleReph) != 0))
                    {
                        RunGlyph reph = _run[rephAt];
                        reph.Mark = true;   // it is positioned by abvm, over the letter before it
                        int target = end;
                        while (target - 1 > rephAt && (_run[target - 1].Indic == IndicBindu || _run[target - 1].Indic == IndicVisarga ||
                                                        _run[target - 1].Indic == IndicSyllableModifier || _run[target - 1].Indic == IndicTone)) target--;
                        _run.RemoveAt(rephAt);
                        _run.Insert(target - 1, reph);
                    }
                }
            }
        }

        private uint ClusterAtLogical(int logical) => logical < _run.Count ? _run[logical].Cluster : uint.MaxValue;
    }
}

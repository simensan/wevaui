// FontEngine expands class kerning into individual pairs. On Unity 6000.4,
// querying again after switching faces retains another native expansion.
// Read each font once and keep a compact, immutable table for this domain,
// including after the document that first used the font has been disposed.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Weva.Native
{
    internal sealed unsafe partial class UnityFontBackend
    {
        private sealed class PairTable
        {
            private readonly long[] _keys;
            private readonly float[] _units;

            public PairTable(GlyphPairAdjustmentRecord[] records)
            {
                var pairs = new Dictionary<long, float>(records?.Length ?? 0);
                foreach (var record in records ?? Array.Empty<GlyphPairAdjustmentRecord>())
                {
                    uint left = record.firstAdjustmentRecord.glyphIndex;
                    uint right = record.secondAdjustmentRecord.glyphIndex;
                    if (left == 0 && right == 0) break;
                    long key = ((long)left << 32) | right;
                    // The first subtable covering a pair wins, including a
                    // zero adjustment that masks a later class adjustment.
                    if (!pairs.ContainsKey(key))
                        pairs.Add(key, record.firstAdjustmentRecord.glyphValueRecord.xAdvance +
                                       record.secondAdjustmentRecord.glyphValueRecord.xAdvance);
                }
                int count = 0;
                foreach (float units in pairs.Values) if (units != 0) ++count;
                _keys = new long[count];
                _units = new float[count];
                int at = 0;
                foreach (var pair in pairs)
                {
                    if (pair.Value == 0) continue;
                    _keys[at] = pair.Key;
                    _units[at++] = pair.Value;
                }
                Array.Sort(_keys, _units);
            }

            public double Units(uint left, uint right)
            {
                int at = Array.BinarySearch(_keys, ((long)left << 32) | right);
                return at >= 0 ? _units[at] : 0;
            }
        }

        private sealed class FontPairTables
        {
            public long Modified, Length;
            public readonly Dictionary<int, PairTable> Faces = new Dictionary<int, PairTable>();
        }

#if UNITY_EDITOR
        private sealed class EditorFontData
        {
            public Hash128 Revision;
            public byte[] Bytes;
        }

        private static readonly Dictionary<Font, EditorFontData> s_editorFontData = new Dictionary<Font, EditorFontData>();

        // Reimport replaces the storage behind a Font asset while FontEngine
        // can still hold its previous face. Loading that asset again crashed
        // GetFaceInfo on 6000.4. Private content-based buffers keep old native
        // faces valid and give changed data a fresh identity, without unloading
        // process-global faces that TextMeshPro or another document may use.
        private static byte[] BytesForEditorAsset(Font font)
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(font);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".ttf" && extension != ".otf" && extension != ".ttc" && extension != ".otc") return null;
            Hash128 revision = UnityEditor.AssetDatabase.GetAssetDependencyHash(path);
            if (!s_editorFontData.TryGetValue(font, out EditorFontData data) || data.Revision != revision)
            {
                try { data = new EditorFontData { Revision = revision, Bytes = SharedFontBytes(File.ReadAllBytes(path)) }; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                s_editorFontData[font] = data;
            }
            return data.Bytes;
        }
#endif

        // Keep font identities stable for as long as FontEngine can retain
        // their native tables. Byte identities are canonicalized by Adopt.
        private static readonly Dictionary<Font, FontPairTables> s_assetPairs = new Dictionary<Font, FontPairTables>();
        private static readonly Dictionary<byte[], FontPairTables> s_bytePairs = new Dictionary<byte[], FontPairTables>();
        private static readonly Dictionary<string, FontPairTables> s_pathPairs = new Dictionary<string, FontPairTables>(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private static FontPairTables TablesFor(Source source)
        {
            FontPairTables tables;
#if UNITY_EDITOR
            if (source.EditorAssetBytes != null)
            {
                if (!s_bytePairs.TryGetValue(source.EditorAssetBytes, out tables))
                    s_bytePairs.Add(source.EditorAssetBytes, tables = new FontPairTables());
            }
            else
#endif
            if (source.Asset != null)
            {
                if (!s_assetPairs.TryGetValue(source.Asset, out tables))
                    s_assetPairs.Add(source.Asset, tables = new FontPairTables());
            }
            else if (!string.IsNullOrEmpty(source.Path))
            {
                string path = Path.GetFullPath(source.Path);
                var file = new FileInfo(path);
                long modified = file.LastWriteTimeUtc.Ticks, length = file.Exists ? file.Length : 0;
                if (!s_pathPairs.TryGetValue(path, out tables) || tables.Modified != modified || tables.Length != length)
                    s_pathPairs[path] = tables = new FontPairTables { Modified = modified, Length = length };
            }
            else if (!s_bytePairs.TryGetValue(source.Bytes, out tables))
            {
                s_bytePairs.Add(source.Bytes, tables = new FontPairTables());
            }
            return tables;
        }

        private double KernOf(Source source, int size, uint left, uint right)
        {
            if (source.Kerning == null)
            {
                var query = PairRecordsMethod();
                if (query == null || !ActivateAt(source, size) || source.UnitsPerEm <= 0) return 0;
                try
                {
                    FontPairTables tables = TablesFor(source);
                    if (!tables.Faces.TryGetValue(source.Index, out source.Kerning))
                    {
                        var records = query.Invoke(null, null) as GlyphPairAdjustmentRecord[];
                        source.Kerning = new PairTable(records);
                        tables.Faces.Add(source.Index, source.Kerning);
                    }
                }
                catch (Exception ex)
                {
                    LastError = "GetAllPairAdjustmentRecords: " + ex.Message;
                    return 0;
                }
            }
            return source.Kerning.Units(left, right) * size / source.UnitsPerEm;
        }
    }
}

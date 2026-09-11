// The layout dump from the Unity host: its JSON matches weva_dump's format
// and rules on a fixture, and, given a manifest (WEVA_NATIVE_DUMP_MANIFEST,
// one "html<TAB>css<TAB>width<TAB>height<TAB>out" line per case), it dumps a
// whole corpus in one editor run for godot-port/hosts/unity/oracle_from_unity.py
// to compare against weva_dump.
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeLayoutDumpTests
    {
        [Test]
        public void Num_FormatsLikeWevaDump()
        {
            Assert.That(NativeLayoutDump.Num(32), Is.EqualTo("32"));
            Assert.That(NativeLayoutDump.Num(20.574), Is.EqualTo("20.574"));
            Assert.That(NativeLayoutDump.Num(3.75975), Is.EqualTo("3.7598"), "half away from zero at four decimals");
            Assert.That(NativeLayoutDump.Num(-0.00001), Is.EqualTo("0"), "never -0");
            Assert.That(NativeLayoutDump.Num(1.5), Is.EqualTo("1.5"));
        }

        [Test]
        public void Dump_SkipsWrappers_CountsDepth_AndUsesTheSyntheticFace()
        {
            using (var doc = new NativeDocument(400, 300))
            using (var fonts = new SyntheticFontBackend())
            {
                fonts.Install(doc);
                doc.LoadHtml("<html><body><div id=\"a\" class=\"box\"><p id=\"t\">Hello</p><span id=\"hidden\" style=\"display:none\">x</span></div><div id=\"b\"></div></body></html>");
                doc.SetCss("body{margin:0}#a{padding:10px}#b{height:5px}p{margin:0;font-size:20px}");
                doc.Update(0);
                List<NativeLayoutDump.Entry> entries = NativeLayoutDump.Collect(doc);
                Assert.That(entries.Count, Is.EqualTo(3), "html and body are not listed; display:none has no box");
                Assert.That(entries[0].Tag, Is.EqualTo("div"));
                Assert.That(entries[0].Id, Is.EqualTo("a"));
                Assert.That(entries[0].Class, Is.EqualTo("box"));
                Assert.That(entries[0].Depth, Is.EqualTo(1), "a top-level div is depth 1");
                Assert.That(entries[1].Tag, Is.EqualTo("p"));
                Assert.That(entries[1].Depth, Is.EqualTo(2));
                Assert.That(entries[2].Id, Is.EqualTo("b"));
                Assert.That(entries[2].Depth, Is.EqualTo(1));
                Assert.That(entries[1].X, Is.EqualTo(10).Within(1e-9));
                Assert.That(entries[1].H, Is.EqualTo(20 * 1.143).Within(1e-6), "the synthetic face lines at 1.143em");
                // "Hello" at 20px: five glyphs at 0.45em.
                doc.LoadHtml("<body><span id=\"s\">Hello</span></body>");
                doc.SetCss("body{margin:0}span{font-size:20px}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#s"), out NativeBounds b));
                Assert.That(b.Width, Is.EqualTo(5 * 0.45 * 20).Within(1e-6), "0.45em per glyph");
                string json = NativeLayoutDump.Dump(doc, "fixture.html", 400, 300);
                Assert.That(json, Does.StartWith("{\n  \"source\": \"fixture.html\",\n  \"width\": 400,\n  \"height\": 300,\n  \"count\": 1,"));
                // The core's walk counts the line box the span sits in, as weva_dump does.
                Assert.That(json, Does.Contain("{\"i\":0,\"depth\":2,\"tag\":\"span\",\"id\":\"s\",\"cls\":\"\",\"path\":\"\",\"x\":0,\"y\":"), json);
            }
        }

        [Test]
        public void Manifest_DumpsEveryCaseForTheOracle()
        {
            string manifest = System.Environment.GetEnvironmentVariable("WEVA_NATIVE_DUMP_MANIFEST");
            Assume.That(!string.IsNullOrEmpty(manifest) && File.Exists(manifest), "WEVA_NATIVE_DUMP_MANIFEST names the cases to dump (value: '" + manifest + "', exists: " + (manifest != null && File.Exists(manifest)) + ")");
            int dumped = 0;
            foreach (string line in File.ReadAllLines(manifest))
            {
                if (line.Trim().Length == 0) continue;
                string[] f = line.Split('\t');
                Assert.That(f.Length, Is.GreaterThanOrEqualTo(5), "html, css, width, height, out");
                string html = f[0], css = f[1];
                int width = int.Parse(f[2]), height = int.Parse(f[3]);
                string outPath = f[4];
                using (var doc = new NativeDocument(width, height))
                using (var fonts = new SyntheticFontBackend())
                {
                    fonts.Install(doc);
                    doc.SetBasePath(Path.GetDirectoryName(Path.GetFullPath(html)));
                    doc.LoadHtml(File.ReadAllText(html));
                    if (css.Length > 0 && File.Exists(css)) doc.SetCss(File.ReadAllText(css));
                    doc.Update(0);
                    string json = NativeLayoutDump.Dump(doc, Path.GetFileName(html), width, height);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                    File.WriteAllText(outPath, json, new System.Text.UTF8Encoding(false));
                    dumped++;
                }
            }
            TestContext.WriteLine(dumped + " cases dumped");
            Assert.That(dumped, Is.GreaterThan(0));
        }
    }
}

using NUnit.Framework;
using Weva.InPlace;

namespace Weva.Tests.InPlace {
    // Coverage for the surgical inline-style editor — the default edit-targeting path of the
    // in-place editor. The invariant under test: every declaration the caller did NOT touch keeps
    // its exact source bytes (whitespace, casing, comments, ordering); only the targeted value
    // changes. See /WEVA_INPLACE_EDITOR_SCOPE.md §4.
    public class InlineStyleEditTests {

        // --- TryGetProperty ---

        [Test]
        public void Get_returns_value_and_no_important() {
            Assert.That(InlineStyleEdit.TryGetProperty("color: red; margin: 0", "color", out var v, out var imp), Is.True);
            Assert.That(v, Is.EqualTo("red"));
            Assert.That(imp, Is.False);
        }

        [Test]
        public void Get_is_case_insensitive_on_property_name() {
            Assert.That(InlineStyleEdit.TryGetProperty("COLOR: red", "color", out var v, out _), Is.True);
            Assert.That(v, Is.EqualTo("red"));
        }

        [Test]
        public void Get_missing_returns_false() {
            Assert.That(InlineStyleEdit.TryGetProperty("color: red", "background", out _, out _), Is.False);
        }

        [Test]
        public void Get_strips_important_and_reports_flag() {
            Assert.That(InlineStyleEdit.TryGetProperty("color: red !important", "color", out var v, out var imp), Is.True);
            Assert.That(v, Is.EqualTo("red"));
            Assert.That(imp, Is.True);
        }

        [Test]
        public void Get_last_occurrence_wins() {
            Assert.That(InlineStyleEdit.TryGetProperty("color: red; color: blue", "color", out var v, out _), Is.True);
            Assert.That(v, Is.EqualTo("blue"));
        }

        [Test]
        public void Get_value_with_semicolon_inside_string_and_parens() {
            Assert.That(InlineStyleEdit.TryGetProperty("background: url(\"a;b.png\") no-repeat", "background", out var v, out _), Is.True);
            Assert.That(v, Is.EqualTo("url(\"a;b.png\") no-repeat"));
        }

        [Test]
        public void Get_custom_property() {
            Assert.That(InlineStyleEdit.TryGetProperty("--brand: #123; color: red", "--brand", out var v, out _), Is.True);
            Assert.That(v, Is.EqualTo("#123"));
        }

        [Test]
        public void Get_on_null_or_empty_is_false() {
            Assert.That(InlineStyleEdit.TryGetProperty(null, "color", out _, out _), Is.False);
            Assert.That(InlineStyleEdit.TryGetProperty("", "color", out _, out _), Is.False);
        }

        // --- SetProperty: replace in place ---

        [Test]
        public void Set_replaces_existing_value_preserving_siblings() {
            Assert.That(InlineStyleEdit.SetProperty("color: red; background: blue", "color", "green"),
                Is.EqualTo("color: green; background: blue"));
        }

        [Test]
        public void Set_preserves_surrounding_whitespace_exactly() {
            // Only the value run ("red") is replaced; the odd spacing around it survives.
            Assert.That(InlineStyleEdit.SetProperty("  color :   red  ;  margin:0", "color", "green"),
                Is.EqualTo("  color :   green  ;  margin:0"));
        }

        [Test]
        public void Set_preserves_original_property_casing() {
            Assert.That(InlineStyleEdit.SetProperty("Background-Color: red", "background-color", "blue"),
                Is.EqualTo("Background-Color: blue"));
        }

        [Test]
        public void Set_updates_only_last_duplicate() {
            Assert.That(InlineStyleEdit.SetProperty("color: red; color: blue", "color", "green"),
                Is.EqualTo("color: red; color: green"));
        }

        [Test]
        public void Set_can_add_important() {
            Assert.That(InlineStyleEdit.SetProperty("color: red", "color", "green", important: true),
                Is.EqualTo("color: green !important"));
        }

        [Test]
        public void Set_replacing_important_value_drops_flag_when_not_requested() {
            Assert.That(InlineStyleEdit.SetProperty("color: red !important", "color", "green"),
                Is.EqualTo("color: green"));
        }

        [Test]
        public void Set_value_with_internal_spaces_preserved() {
            Assert.That(InlineStyleEdit.SetProperty("margin: 0", "margin", "1px 2px 3px 4px"),
                Is.EqualTo("margin: 1px 2px 3px 4px"));
        }

        // --- SetProperty: append ---

        [Test]
        public void Set_appends_when_absent() {
            Assert.That(InlineStyleEdit.SetProperty("color: red", "margin", "0"),
                Is.EqualTo("color: red; margin: 0"));
        }

        [Test]
        public void Set_append_handles_trailing_semicolon() {
            Assert.That(InlineStyleEdit.SetProperty("color: red;", "margin", "0"),
                Is.EqualTo("color: red; margin: 0"));
        }

        [Test]
        public void Set_append_handles_trailing_semicolon_and_whitespace() {
            Assert.That(InlineStyleEdit.SetProperty("color: red;  ", "margin", "0"),
                Is.EqualTo("color: red; margin: 0"));
        }

        [Test]
        public void Set_on_empty_creates_single_declaration() {
            Assert.That(InlineStyleEdit.SetProperty("", "color", "red"), Is.EqualTo("color: red"));
            Assert.That(InlineStyleEdit.SetProperty(null, "color", "red"), Is.EqualTo("color: red"));
        }

        [Test]
        public void Set_blank_value_removes() {
            Assert.That(InlineStyleEdit.SetProperty("color: red; margin: 0", "color", ""),
                Is.EqualTo("margin: 0"));
            Assert.That(InlineStyleEdit.SetProperty("color: red; margin: 0", "color", null),
                Is.EqualTo("margin: 0"));
        }

        // --- RemoveProperty ---

        [Test]
        public void Remove_middle_declaration() {
            Assert.That(InlineStyleEdit.RemoveProperty("color: red; margin: 0; padding: 8px", "margin"),
                Is.EqualTo("color: red; padding: 8px"));
        }

        [Test]
        public void Remove_first_declaration() {
            Assert.That(InlineStyleEdit.RemoveProperty("color: red; margin: 0", "color"),
                Is.EqualTo("margin: 0"));
        }

        [Test]
        public void Remove_last_declaration() {
            Assert.That(InlineStyleEdit.RemoveProperty("color: red; margin: 0", "margin"),
                Is.EqualTo("color: red;"));
        }

        [Test]
        public void Remove_all_duplicates() {
            Assert.That(InlineStyleEdit.RemoveProperty("color: red; color: blue; margin: 0", "color"),
                Is.EqualTo("margin: 0"));
        }

        [Test]
        public void Remove_absent_is_unchanged() {
            const string s = "color: red; margin: 0";
            Assert.That(InlineStyleEdit.RemoveProperty(s, "padding"), Is.EqualTo(s));
        }

        [Test]
        public void Remove_is_case_insensitive() {
            Assert.That(InlineStyleEdit.RemoveProperty("COLOR: red; margin: 0", "color"),
                Is.EqualTo("margin: 0"));
        }

        // --- robustness ---

        [Test]
        public void Malformed_segments_are_preserved_on_unrelated_remove() {
            // The empty leading segments and the bare `;;` should survive removing `margin`.
            Assert.That(InlineStyleEdit.RemoveProperty(";;color: red; margin: 0", "margin"),
                Is.EqualTo(";;color: red;"));
        }

        [Test]
        public void Set_does_not_split_on_semicolon_inside_value() {
            Assert.That(InlineStyleEdit.SetProperty("background: url(\"a;b.png\")", "color", "red"),
                Is.EqualTo("background: url(\"a;b.png\"); color: red"));
        }

        [Test]
        public void Round_trip_set_same_value_is_stable() {
            const string s = "  color :   red  ;  margin:0";
            string once = InlineStyleEdit.SetProperty(s, "color", "red");
            string twice = InlineStyleEdit.SetProperty(once, "color", "red");
            Assert.That(twice, Is.EqualTo(once));
        }
    }
}

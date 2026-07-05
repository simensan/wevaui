using Weva.Css;
using Weva.Css.Cascade;
using Weva.Parsing;

namespace Weva.Forms {
    // Form-control UA defaults — appended on top of UserAgentStylesheet (which
    // ships the generic block/inline display rules). Kept in its own file so
    // the three sister tasks (URP, TextCore, scroll) don't conflict-merge with
    // shared UserAgentStylesheet.cs. The cascade engine consumes both sheets
    // via OriginatedStylesheet.UserAgent at the same origin level, so order
    // among UA stylesheets resolves by document order — caller appends this
    // one after the base sheet.
    public static class FormControlStylesheet {
        public const string Source = @"
input { display: inline-block; box-sizing: border-box; width: 218px; height: 34px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; font: inherit; }
input[type=""checkbox""], input[type=""radio""] { width: 16px; height: 16px; padding: 0; margin: 0 4px 0 0; }
input[type=""radio""] { border-radius: 8px; }
textarea { display: inline-block; box-sizing: border-box; width: 218px; height: 90px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; font: inherit; }
select { display: inline-block; box-sizing: border-box; min-width: 218px; height: 34px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; }
/* CSS UA: a closed `<select>` renders only the selected option's text via
   its own paint path; child `<option>` elements are NOT laid out as block
   boxes in the document flow. Without this rule the engine stacks every
   option below the select like generic blocks, displacing surrounding
   layout by hundreds of pixels per dropdown (e.g. a settings page with
   three selects shifts every following row ~220px up/down). The author
   stylesheet can still override `option { display: ... }` for custom
   listbox surfaces. */
option { display: none; }
optgroup { display: none; }
/* select[size] / select[multiple] expose a listbox surface where options
   ARE laid out in flow; uncloak them in those cases. */
select[size] option, select[multiple] option { display: block; }
select[size] optgroup, select[multiple] optgroup { display: block; }
dialog { display: none; position: fixed; padding: 16px; border: 1px solid #ccc; border-radius: 8px; background: white; }
dialog[open] { display: block; }
[popover] { display: none; position: fixed; padding: 8px 16px; border: 1px solid #ccc; border-radius: 4px; background: white; }
[popover][data-popover-open] { display: block; }
::backdrop { background: rgba(0, 0, 0, 0.5); }
/* CSS UI 4: only paint the focus ring on keyboard-driven focus to avoid the
   sticky outline mouse-down users complained about for years. */
:focus-visible { outline: 2px solid #2563eb; outline-offset: 2px; }
:disabled { opacity: 0.5; cursor: not-allowed; }

/* range slider — this rule is just the track footprint (size + rail
   background). InputRenderer.DrawRangeTrack paints the accent-colored fill
   and the round thumb on top of it at paint time. */
input[type=""range""] { width: 200px; height: 18px; padding: 0; border: 1px solid #ccc; border-radius: 9px; background: #e5e7eb; cursor: pointer; }

/* tooltips injected by TooltipManager. Authors can fully restyle by
   targeting `.ui-tooltip`; defaults below match a typical neutral pill. */
.ui-tooltip { padding: 4px 8px; border-radius: 4px; background: rgba(15, 23, 42, 0.95); color: #f8fafc; font-size: 12px; line-height: 1.3; max-width: 240px; pointer-events: none; }

/* context / dropdown menu. Solid card with rounded corners, items sized
   for both pointer and keyboard activation. The is-focused and is-disabled
   modifier classes are owned by ContextMenu's keyboard navigation logic. */
.ui-menu { display: flex; flex-direction: column; min-width: 160px; padding: 4px 0; border: 1px solid #d1d5db; border-radius: 6px; background: white; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15); font-size: 13px; }
.ui-menu-item { display: flex; align-items: center; padding: 6px 12px; cursor: pointer; gap: 8px; }
.ui-menu-item:hover { background: #f3f4f6; }
.ui-menu-item.is-focused { background: #e0e7ff; }
.ui-menu-item.is-disabled { opacity: 0.5; cursor: not-allowed; }
.ui-menu-item .ui-menu-label { flex: 1; }
.ui-menu-item .ui-menu-shortcut { color: #6b7280; font-size: 12px; }
.ui-menu-separator { height: 1px; background: #e5e7eb; margin: 4px 0; }
";

        public static OriginatedStylesheet Parse() {
            var sheet = CssParser.Parse(Source, new ParseOptions { ThrowOnError = true });
            return OriginatedStylesheet.UserAgent(sheet);
        }
    }
}

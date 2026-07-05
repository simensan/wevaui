using System.Collections.Generic;

namespace Weva.Designer
{
    /// <summary>
    /// One node in the Design Document tree — the authoring IR the editor mutates.
    /// It is deliberately NOT a CSS property bag: it carries the small, opinionated
    /// set of concepts a designer manipulates (layout / sizing / spacing / fill /
    /// text), and <see cref="DesignCompiler"/> lowers it to Weva HTML/CSS.
    ///
    /// Geometry (padding, gap, width, height, radius, font-size) is raw px in M1;
    /// colors support token references (see <see cref="DesignTokens"/>). Token-based
    /// spacing/type arrives in M4.
    /// </summary>
    public sealed class DesignNode
    {
        /// <summary>Author-facing name; emitted as <c>data-name</c> for selection/debugging.</summary>
        public string Name;

        /// <summary>
        /// Literal text content. When set the node is a text element (defaults its
        /// tag to a span-like div with text); when null the node is a container.
        /// </summary>
        public string Text;

        public readonly List<DesignNode> Children = new List<DesignNode>();

        // --- Layout (this node as a container) ---
        public LayoutMode Layout = LayoutMode.None;
        public MainAlign MainAlign = MainAlign.Start;
        // Start, not Stretch: a Hug child should hug, not be stretched by CSS's
        // default align-items:stretch. Fill children opt back into stretch per-axis.
        public CrossAlign CrossAlign = CrossAlign.Start;
        /// <summary>Number of equal columns when <see cref="Layout"/> is Grid; ≤1 ⇒ a single column.</summary>
        public int GridColumns;
        /// <summary>Let Row/Column children wrap onto new lines when they overflow (tag lists, button groups).</summary>
        public bool Wrap;
        public Dim Gap;                    // px or spacing token, between children (Row/Column/Grid)
        public Dim PadTop, PadRight, PadBottom, PadLeft; // px or spacing tokens

        // --- Sizing (this node as a child of an auto-layout parent) ---
        public SizeMode WidthMode = SizeMode.Hug;
        public SizeMode HeightMode = SizeMode.Hug;
        public double Width, Height;       // px, used when the matching mode is Fixed
        // Optional size constraints (px; 0 = unset). Apply regardless of sizing mode, so a
        // Fill child can be capped ("fill, but never exceed 400px") or floored ("at least
        // 200px"). These are the constraint-driven escape hatch the responsive model leans on.
        public double MinWidth, MaxWidth, MinHeight, MaxHeight;
        /// <summary>Width÷height ratio (e.g. 16.0/9.0); 0 = unset. Lets one axis derive from the other.</summary>
        public double AspectRatio;

        // --- Placement (out-of-flow overlays) ---
        /// <summary>InFlow (default) or Absolute. Absolute pins to the parent box via the offsets below.</summary>
        public Position Position = Position.InFlow;
        // Edge offsets for an Absolute node (px or spacing token). Null = that edge is unpinned;
        // a set value of 0 still pins to the edge. Used only when <see cref="Position"/> is Absolute.
        public Dim? OffTop, OffRight, OffBottom, OffLeft;

        public bool IsAbsolute => Position == Position.Absolute;

        // --- Style ---
        /// <summary>Background color: a raw CSS color, or <c>{token-name}</c> for a color token.</summary>
        public string Fill;
        /// <summary>Background image URL/path; null = none. Rendered over <see cref="Fill"/>.</summary>
        public string BackgroundImage;
        /// <summary>How the background image fills the box (Cover by default).</summary>
        public BackgroundSize BackgroundSize = BackgroundSize.Cover;
        /// <summary>Border (stroke) color: a raw CSS color, or <c>{token-name}</c>. Null = no border.</summary>
        public string Stroke;
        /// <summary>Border (stroke) width in px. Used only when <see cref="Stroke"/> is set; ≤0 ⇒ a 1px default.</summary>
        public double StrokeWidth;
        public Dim Radius;                 // px or radius token — the uniform corner radius
        // Optional per-corner overrides (null = use the uniform Radius). When any is set the
        // compiler emits the 4-value border-radius shorthand (TL TR BR BL). For e.g. a tab
        // with only its top corners rounded.
        public Dim? RadiusTopLeft, RadiusTopRight, RadiusBottomRight, RadiusBottomLeft;
        /// <summary>Clip/scroll behaviour for content that overflows this node's box.</summary>
        public Overflow Overflow = Overflow.Visible;

        public bool HasPerCornerRadius =>
            RadiusTopLeft.HasValue || RadiusTopRight.HasValue
            || RadiusBottomRight.HasValue || RadiusBottomLeft.HasValue;
        public double Opacity = 1;
        /// <summary>Drop shadow: a raw CSS box-shadow, or <c>{token-name}</c> for a shadow token.</summary>
        public string Shadow;
        /// <summary>Mouse cursor over this node (Pointer = clickable affordance).</summary>
        public Cursor Cursor = Cursor.Default;
        /// <summary>Animate style changes (e.g. into hover/pressed) over this many ms; 0 = instant.</summary>
        public double TransitionMs;
        /// <summary>Clockwise rotation in degrees; 0 = none. A paint transform (doesn't affect layout).</summary>
        public double Rotation;
        /// <summary>Uniform scale factor; 1 = none. A paint transform (doesn't affect layout).</summary>
        public double Scale = 1;

        // --- Text style (when Text is set) ---
        /// <summary>Text color: raw CSS color or <c>{token-name}</c>.</summary>
        public string TextColor;
        public Dim FontSize;               // px or type-scale token; 0 = inherit
        public FontWeight FontWeight = FontWeight.Normal;
        public bool Italic;
        public TextAlign TextAlign = TextAlign.Start;
        public double LineHeight;          // unitless multiplier (e.g. 1.5); 0 = inherit
        public double LetterSpacing;       // px; 0 = normal (may be negative to tighten)
        public TextTransform TextTransform = TextTransform.None;
        public TextDecoration TextDecoration = TextDecoration.None;
        /// <summary>
        /// Drop shadow behind the glyphs (legibility over busy backgrounds — the iconic
        /// HUD number / title outline). A raw CSS <c>text-shadow</c> value (e.g.
        /// <c>"0 1px 2px rgba(0,0,0,.6)"</c>) or <c>{token}</c> for a shadow token; null = none.
        /// Distinct from <see cref="Shadow"/> (box-shadow on the element's box).
        /// </summary>
        public string TextShadow;

        // --- Interactive states (hover/pressed/focus/disabled) ---
        /// <summary>Per-state overrides; null until a state is added. Use the helpers below.</summary>
        public Dictionary<InteractionState, StateStyle> States;

        /// <summary>Stable iteration order for deterministic compile/serialize output.</summary>
        public static readonly InteractionState[] AllStates =
        {
            InteractionState.Hover, InteractionState.Pressed, InteractionState.Focus, InteractionState.Disabled,
        };

        public bool HasStates => States != null && States.Count > 0;

        /// <summary>The override for a state, or null if absent.</summary>
        public StateStyle GetState(InteractionState s)
            => States != null && States.TryGetValue(s, out StateStyle v) ? v : null;

        /// <summary>Get the override for a state, creating an empty one if needed.</summary>
        public StateStyle State(InteractionState s)
        {
            States ??= new Dictionary<InteractionState, StateStyle>();
            if (!States.TryGetValue(s, out StateStyle v)) { v = new StateStyle(); States[s] = v; }
            return v;
        }

        /// <summary>Set (or, with null, remove) a state override.</summary>
        public void SetStateStyle(InteractionState s, StateStyle style)
        {
            if (style == null) { States?.Remove(s); return; }
            States ??= new Dictionary<InteractionState, StateStyle>();
            States[s] = style;
        }

        // --- Component instance ---
        /// <summary>If set, this node is an instance of the named component (expanded at compile).</summary>
        public string ComponentRef;
        /// <summary>Selected variant of the referenced component, or null for defaults.</summary>
        public string Variant;
        /// <summary>Instance-level prop overrides (win over variant + component defaults).</summary>
        public Dictionary<string, string> Props;
        /// <summary>Marks this template node as the slot that receives an instance's children.</summary>
        public bool IsSlot;

        public bool IsInstance => !string.IsNullOrEmpty(ComponentRef);

        public void SetProp(string name, string value)
        {
            Props ??= new Dictionary<string, string>();
            Props[name] = value;
        }

        // --- Data binding ---
        /// <summary>Data-binding for this node; null until used. Use <see cref="Bind"/>.</summary>
        public NodeBinding Binding;

        public bool HasBinding => Binding != null && !Binding.IsEmpty;

        /// <summary>Get the binding, creating an empty one if needed.</summary>
        public NodeBinding Bind() => Binding ??= new NodeBinding();

        public DesignNode() { }

        public DesignNode(string name) { Name = name; }

        // --- Fluent builders (used by tests and, later, importers) ---

        public DesignNode Add(DesignNode child)
        {
            Children.Add(child);
            return this;
        }

        public DesignNode SetPadding(double all)
        {
            PadTop = PadRight = PadBottom = PadLeft = all;
            return this;
        }

        public DesignNode SetSize(SizeMode width, SizeMode height)
        {
            WidthMode = width;
            HeightMode = height;
            return this;
        }

        public DesignNode SetFixedSize(double width, double height)
        {
            WidthMode = SizeMode.Fixed;
            HeightMode = SizeMode.Fixed;
            Width = width;
            Height = height;
            return this;
        }

        public bool IsText => Text != null;
        public bool IsContainer => Layout != LayoutMode.None;

        /// <summary>
        /// Deep copy of this node and its whole subtree (used by clipboard duplicate/
        /// paste and, later, component instancing). The clone shares no references with
        /// the original, so editing one never affects the other.
        /// </summary>
        public DesignNode Clone()
        {
            var c = new DesignNode
            {
                Name = Name,
                Text = Text,
                Layout = Layout,
                MainAlign = MainAlign,
                CrossAlign = CrossAlign,
                GridColumns = GridColumns,
                Wrap = Wrap,
                Gap = Gap,
                PadTop = PadTop,
                PadRight = PadRight,
                PadBottom = PadBottom,
                PadLeft = PadLeft,
                WidthMode = WidthMode,
                HeightMode = HeightMode,
                Width = Width,
                Height = Height,
                MinWidth = MinWidth,
                MaxWidth = MaxWidth,
                MinHeight = MinHeight,
                MaxHeight = MaxHeight,
                AspectRatio = AspectRatio,
                Position = Position,
                OffTop = OffTop,
                OffRight = OffRight,
                OffBottom = OffBottom,
                OffLeft = OffLeft,
                Fill = Fill,
                BackgroundImage = BackgroundImage,
                BackgroundSize = BackgroundSize,
                Stroke = Stroke,
                StrokeWidth = StrokeWidth,
                Radius = Radius,
                RadiusTopLeft = RadiusTopLeft,
                RadiusTopRight = RadiusTopRight,
                RadiusBottomRight = RadiusBottomRight,
                RadiusBottomLeft = RadiusBottomLeft,
                Opacity = Opacity,
                Shadow = Shadow,
                Cursor = Cursor,
                TransitionMs = TransitionMs,
                Rotation = Rotation,
                Scale = Scale,
                Overflow = Overflow,
                TextColor = TextColor,
                FontSize = FontSize,
                FontWeight = FontWeight,
                Italic = Italic,
                TextAlign = TextAlign,
                LineHeight = LineHeight,
                LetterSpacing = LetterSpacing,
                TextTransform = TextTransform,
                TextDecoration = TextDecoration,
                TextShadow = TextShadow,
            };
            if (States != null)
            {
                c.States = new Dictionary<InteractionState, StateStyle>();
                foreach (var kv in States) c.States[kv.Key] = kv.Value.Clone();
            }
            if (Binding != null) c.Binding = Binding.Clone();
            c.ComponentRef = ComponentRef;
            c.Variant = Variant;
            c.IsSlot = IsSlot;
            if (Props != null) c.Props = new Dictionary<string, string>(Props);
            for (int i = 0; i < Children.Count; i++)
                c.Children.Add(Children[i].Clone());
            return c;
        }
    }
}

namespace Weva.Designer
{
    /// <summary>
    /// How a <see cref="DesignNode"/> arranges its children. This is the
    /// designer-facing "auto layout" vocabulary — it compiles to flex/grid/block
    /// in <see cref="DesignCompiler"/>, but the author never sees those words.
    /// </summary>
    public enum LayoutMode
    {
        /// <summary>Children flow as normal block boxes (no auto-layout). "Free".</summary>
        None,
        /// <summary>Children stack left → right. Compiles to <c>flex-direction: row</c>.</summary>
        Row,
        /// <summary>Children stack top → bottom. Compiles to <c>flex-direction: column</c>.</summary>
        Column,
        /// <summary>Two-dimensional grid. (Compiler support lands in a later milestone.)</summary>
        Grid,
    }

    /// <summary>
    /// How a node sizes itself along one axis, as a child of an auto-layout parent.
    /// This single three-way control replaces width/height/flex-grow/shrink/min/max
    /// in the author's mental model.
    /// </summary>
    public enum SizeMode
    {
        /// <summary>Fixed size in px (the node's Width/Height field).</summary>
        Fixed,
        /// <summary>Shrink to fit contents. Compiles to <c>auto</c>.</summary>
        Hug,
        /// <summary>Grow to fill the parent. Compiles to <c>flex: 1</c> (main) / <c>align-self: stretch</c> (cross).</summary>
        Fill,
    }

    /// <summary>Alignment of children along the layout's main axis.</summary>
    public enum MainAlign
    {
        Start,
        Center,
        End,
        SpaceBetween,
    }

    /// <summary>Alignment of children along the layout's cross axis.</summary>
    public enum CrossAlign
    {
        Start,
        Center,
        End,
        Stretch,
    }

    /// <summary>
    /// How a node is placed in its parent. InFlow participates in the parent's auto-layout;
    /// Absolute is lifted out of flow and pinned to the parent's box via top/right/bottom/
    /// left offsets (the parent becomes the positioning context) — overlays, HUD badges,
    /// corner close-buttons. Compiles to CSS <c>position: static</c> / <c>absolute</c>.
    /// </summary>
    public enum Position
    {
        InFlow,
        Absolute,
    }

    /// <summary>
    /// How a background image fills its box. Compiles to CSS <c>background-size</c>:
    /// Cover (fill, cropping overflow — the usual choice for panels/thumbnails), Contain
    /// (fit whole image, letterboxing), Stretch (distort to exactly fill, = <c>100% 100%</c>).
    /// </summary>
    public enum BackgroundSize
    {
        Cover,
        Contain,
        Stretch,
    }

    /// <summary>
    /// Mouse cursor shown over a node. Compiles to CSS <c>cursor</c> — Pointer is the
    /// "this is clickable" affordance for buttons and links.
    /// </summary>
    public enum Cursor
    {
        Default,
        Pointer,
    }

    /// <summary>
    /// What happens to content that overflows a node's box. Compiles to CSS
    /// <c>overflow</c>: Visible (default, no clip), Clip (<c>hidden</c> — e.g. a card that
    /// crops its image to its rounded corners), Scroll (<c>auto</c> — a scrollable region).
    /// </summary>
    public enum Overflow
    {
        Visible,
        Clip,
        Scroll,
    }

    /// <summary>
    /// Designer-facing font weight. Compiles to the numeric CSS <c>font-weight</c>
    /// (Normal→400, Medium→500, SemiBold→600, Bold→700) — the author picks a name.
    /// </summary>
    public enum FontWeight
    {
        Normal,
        Medium,
        SemiBold,
        Bold,
    }

    /// <summary>
    /// Horizontal text alignment. Compiles to the logical-direction-aware CSS
    /// <c>text-align</c> (Start/Center/End/Justify), so it does the right thing in RTL.
    /// </summary>
    public enum TextAlign
    {
        Start,
        Center,
        End,
        Justify,
    }

    /// <summary>
    /// Letter-case transform applied to text without changing the underlying content.
    /// Compiles to CSS <c>text-transform</c> — e.g. UPPERCASE button labels.
    /// </summary>
    public enum TextTransform
    {
        None,
        Uppercase,
        Lowercase,
        Capitalize,
    }

    /// <summary>
    /// A line drawn through text. Compiles to CSS <c>text-decoration</c> — underline for
    /// links, line-through for struck-out/sale prices.
    /// </summary>
    public enum TextDecoration
    {
        None,
        Underline,
        LineThrough,
    }

    /// <summary>
    /// An interactive state a node can be styled for. The designer toggles these as
    /// chips and restyles visually; the compiler lowers them to pseudo-classes
    /// (hover→:hover, pressed→:active, focus→:focus) or a state class
    /// (disabled→.is-disabled, toggled by app/binding) — the author never writes them.
    /// </summary>
    public enum InteractionState
    {
        Hover,
        Pressed,
        Focus,
        Disabled,
    }
}

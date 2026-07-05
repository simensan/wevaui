namespace Weva.Designer
{
    /// <summary>
    /// Per-state style overrides for a <see cref="DesignNode"/> (the look it takes when
    /// hovered / pressed / focused / disabled). Only the fields the designer changed are
    /// set; everything else inherits the base style. Compiles to a pseudo-class rule.
    /// </summary>
    public sealed class StateStyle
    {
        public string Fill;       // raw color or {token}
        public string TextColor;  // raw color or {token}
        public string Shadow;     // raw box-shadow or {token}
        public string Stroke;     // border color: raw color or {token}
        public double? StrokeWidth; // border width in px (overrides base)
        public Dim? Radius;
        public double? Opacity;
        public TextDecoration? TextDecoration; // e.g. underline a link on hover
        public FontWeight? FontWeight;         // e.g. bold on hover

        public bool IsEmpty =>
            Fill == null && TextColor == null && Shadow == null && Stroke == null
            && StrokeWidth == null && Radius == null && Opacity == null
            && TextDecoration == null && FontWeight == null;

        public StateStyle Clone() => new StateStyle
        {
            Fill = Fill,
            TextColor = TextColor,
            Shadow = Shadow,
            Stroke = Stroke,
            StrokeWidth = StrokeWidth,
            Radius = Radius,
            Opacity = Opacity,
            TextDecoration = TextDecoration,
            FontWeight = FontWeight,
        };
    }
}

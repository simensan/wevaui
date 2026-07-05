namespace Weva.Layout.Grid {
    // Grid-specific justify-content. Distinct from Weva.Layout.Flex.JustifyContent
    // because grid's values per CSS Box Alignment (start/end/center/stretch/space-*)
    // differ from flex's traditional flex-start / flex-end vocabulary.
    public enum JustifyContent {
        Start,
        End,
        Center,
        Stretch,
        SpaceBetween,
        SpaceAround,
        SpaceEvenly
    }
}

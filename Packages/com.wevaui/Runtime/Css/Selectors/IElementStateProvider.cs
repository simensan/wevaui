using Weva.Dom;

namespace Weva.Css.Selectors {
    public interface IElementStateProvider {
        ElementState GetState(Element e);

        // Default-implemented so existing implementations (InteractionStateProvider,
        // user-defined fakes in tests) compile unchanged. Implementations that want to
        // participate in cascade caching should override this and bump the value
        // whenever GetState would return a different result for any element.
        long Version => 0;
    }
}

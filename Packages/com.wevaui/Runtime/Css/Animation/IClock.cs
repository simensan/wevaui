using Weva.Events;

namespace Weva.Css.Animation {
    // We reuse IUIClock from Runtime/Events to avoid two parallel clock interfaces.
    // CssAnimationRunner accepts IUIClock directly; this file exists so the spec's
    // "IClock" name resolves into Weva.Css.Animation as a plain alias.
    public interface IClock : IUIClock { }
}

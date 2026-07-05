namespace Weva.Paint.Images {
    // Looks up an `IImageSource` by handle string. Authors write `<img
    // src="ui/heart-icon">` or `background-image: url("ui/hud/frame")`;
    // game code registers handle → backend texture pairs at startup so
    // the renderer can resolve them at submit time. The framework treats
    // handles as opaque strings — they're not filesystem paths and we
    // don't enforce any naming scheme.
    //
    // Why this is an interface instead of just a concrete dictionary:
    // some games will want lazy-load / addressable-asset wiring (Unity's
    // Addressables, async sprite atlases). A registry that always returns
    // synchronously can implement `TryResolve`; an async-loading registry
    // can return false (image renders as "missing" placeholder) and kick
    // off a background load.
    public interface IImageRegistry {
        bool TryResolve(string handle, out IImageSource source);
    }

    // Optional invalidation contract for registries whose contents can change
    // after a WevaDocument has already painted. Async icon/addressable loaders
    // should bump Version when a handle begins or stops resolving so retained
    // paint caches repaint without requiring a DOM mutation.
    public interface IVersionedImageRegistry : IImageRegistry {
        int Version { get; }
    }
}

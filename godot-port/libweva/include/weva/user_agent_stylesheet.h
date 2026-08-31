#pragma once
#include <string_view>

// Ports Runtime/Css/UserAgentStylesheet.cs. Verbatim CSS source, fed to the
// ordinary parser at DeclarationOrigin::UserAgent.
//
// Load-bearing, not cosmetic: the initial value of `display` is `inline`, so
// without this every element in the document — `html` and `body` included —
// is an inline box and block layout has nothing to lay out.
//
// It is NOT the browser default. The comments inside say why: a Unity runtime
// always paints into a fixed viewport, so `html, body` fill it with no margin
// and `overflow: hidden`, where a browser gives body an 8px margin and lets it
// size to content. Authors writing `height: 100%` get the viewport.

namespace weva {

std::string_view user_agent_stylesheet_source();

} // namespace weva

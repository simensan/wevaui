#pragma once
#include <map>
#include <string>
#include <string_view>

// Ports Runtime/Css/Cascade/{EnvResolver,AttrResolver,EnvironmentVariables}.cs.

namespace weva {

class Element;

// UA-supplied environment variables (CSS Environment Variables L1). These are
// NOT author-defined — env() reads only from this table, never from custom
// properties. Pre-seeded with safe-area-inset-{top,right,bottom,left} at 0px.
class EnvironmentVariables {
public:
    static EnvironmentVariables& instance();
    void set(std::string_view name, std::string_view value);
    bool get(std::string_view name, std::string* out) const;
    void reset_to_defaults();

private:
    EnvironmentVariables();
    std::map<std::string, std::string> vars_;
};

// Resolves env() references. Returns false when a reference cannot resolve and
// has no usable fallback — like var(), that taints the whole declaration and
// the cascade must drop it.
bool resolve_env(std::string_view value, std::string* resolved);

// Resolves attr() references against `element`.
//
// Unlike var()/env(), a missing attribute or a value that will not format as
// the requested type falls back to the EMPTY STRING rather than invalidating
// the declaration — that is what the C# does, and it is the older CSS 2.1
// attr() behaviour rather than the Values L5 rules.
std::string resolve_attr(std::string_view value, const Element& element);

} // namespace weva

#include "weva/keyframes.h"

#include "weva/animation.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>

namespace weva {

namespace {

std::string_view trim(std::string_view s) {
    const auto space = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    while (!s.empty() && space(s.front())) s.remove_prefix(1);
    while (!s.empty() && space(s.back())) s.remove_suffix(1);
    return s;
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

// A keyframe selector: `from`, `to`, or a percentage. One rule may carry
// several -- `0%, 100% { ... }` is how a loop states its ends together.
bool parse_offset(std::string_view raw, double* out) {
    raw = trim(raw);
    if (iequals(raw, "from")) { *out = 0; return true; }
    if (iequals(raw, "to")) { *out = 1; return true; }
    if (raw.size() < 2 || raw.back() != '%') return false;
    raw.remove_suffix(1);
    if (raw.size() > 31) return false;
    char buf[32];
    std::memcpy(buf, raw.data(), raw.size());
    buf[raw.size()] = '\0';
    char* end = nullptr;
    const double v = std::strtod(buf, &end);
    if (end == buf || *end != '\0') return false;
    *out = v / 100.0;
    return true;
}

}   // namespace

void collect_keyframes(const Stylesheet& sheet, std::map<std::string, KeyframeAnimation>* out) {
    if (!out) return;
    for (const RulePtr& r : sheet.rules) {
        if (!r || r->kind() != RuleKind::At) continue;
        const auto& at = static_cast<const GenericAtRule&>(*r);
        if (!iequals(at.name, "keyframes")) continue;
        const std::string_view name = trim(at.prelude);
        if (name.empty()) continue;

        KeyframeAnimation animation;
        animation.name = std::string(name);
        for (const RulePtr& nested : at.nested_rules) {
            if (!nested || nested->kind() != RuleKind::Style) continue;
            const auto& frame_rule = static_cast<const StyleRule&>(*nested);
            for (const std::string& selector : frame_rule.selectors) {
                double offset = 0;
                if (!parse_offset(selector, &offset)) continue;
                Keyframe frame;
                frame.offset = std::clamp(offset, 0.0, 1.0);
                frame.declarations = frame_rule.declarations;
                animation.frames.push_back(std::move(frame));
            }
        }
        if (animation.frames.empty()) continue;
        std::stable_sort(animation.frames.begin(), animation.frames.end(),
                         [](const Keyframe& a, const Keyframe& b) { return a.offset < b.offset; });
        for (const Keyframe& f : animation.frames) {
            for (const Declaration& d : f.declarations) {
                if (std::find(animation.properties.begin(), animation.properties.end(),
                              d.property) == animation.properties.end()) {
                    animation.properties.push_back(d.property);
                }
            }
        }
        // A later @keyframes of the same name replaces the earlier one whole.
        (*out)[animation.name] = std::move(animation);
    }
}

bool keyframe_value_at(const KeyframeAnimation& animation, std::string_view property, double t,
                       std::string* out) {
    if (!out) return false;
    const Keyframe* before = nullptr;
    const Keyframe* after = nullptr;
    for (const Keyframe& f : animation.frames) {
        const Declaration* d = nullptr;
        for (const Declaration& candidate : f.declarations) {
            if (candidate.property == property) d = &candidate;
        }
        // A frame that does not mention the property does not bound it. CSS
        // Animations L1 §3: the value comes from the nearest frames that DO,
        // which is what lets `50% { opacity: .4 }` sit among frames that only
        // set a colour.
        if (!d) continue;
        if (f.offset <= t) before = &f;
        if (f.offset >= t && !after) after = &f;
    }
    const auto value_of = [&](const Keyframe* f) -> std::string_view {
        for (const Declaration& d : f->declarations) {
            if (d.property == property) return d.value_text;
        }
        return {};
    };
    if (!before && !after) return false;
    if (!before) { *out = std::string(value_of(after)); return true; }
    if (!after) { *out = std::string(value_of(before)); return true; }
    if (before == after || after->offset <= before->offset) {
        *out = std::string(value_of(after));
        return true;
    }
    const double local = (t - before->offset) / (after->offset - before->offset);
    return interpolate_css(value_of(before), value_of(after), local, out);
}

}   // namespace weva

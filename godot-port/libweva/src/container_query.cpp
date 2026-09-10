#include "weva/container_query.h"
#include "weva/css_calc.h"
#include <cmath>
#include <string>

namespace weva {
namespace {
bool ws(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f'; }
std::string_view trim(std::string_view s) {
    while (!s.empty() && ws(s.front())) s.remove_prefix(1);
    while (!s.empty() && ws(s.back())) s.remove_suffix(1);
    return s;
}
std::string lower(std::string_view s) {
    std::string r(s);
    for (char& c : r) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return r;
}
bool word(std::string_view s, size_t i, std::string_view w) {
    return s.substr(i,w.size()) == w && (i == 0 || ws(s[i-1]) || s[i-1] == ')') &&
        (i+w.size() == s.size() || ws(s[i+w.size()]) || s[i+w.size()] == '(');
}
QueryTruth invert(QueryTruth v) {
    return v == QueryTruth::Unknown ? v : v == QueryTruth::True ? QueryTruth::False : QueryTruth::True;
}
bool number(std::string_view s, double* out) {
    CssParseError error;
    auto v = parse_css_value(trim(s), &error);
    if (!v || v->kind() != CssValueKind::Number) return false;
    *out = static_cast<const CssNumber&>(*v).value;
    return std::isfinite(*out);
}
}

bool ContainerSizeQuery::parse(std::string_view condition) {
    nodes_.clear(); axes_ = 0;
    // Feature names and boolean operators are ASCII-insensitive. Container
    // names are parsed by the caller and must not be folded here.
    const auto text = lower(trim(condition));
    root_ = expression(text, 0);
    return root_ >= 0;
}

int ContainerSizeQuery::expression(std::string_view s, unsigned depth) {
    s = trim(s);
    if (s.empty() || depth > 64) return -1;
    int nesting = 0;
    size_t first = std::string_view::npos;
    bool conjunction = false;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '(') ++nesting;
        else if (s[i] == ')') { if (--nesting < 0) return -1; }
        else if (nesting == 0 && (word(s,i,"and") || word(s,i,"or"))) {
            const bool is_and = word(s,i,"and");
            if (first == std::string_view::npos) { first = i; conjunction = is_and; }
            else if (is_and != conjunction) return -1; // mixed operators need a group
        }
    }
    if (nesting != 0) return -1;
    if (first != std::string_view::npos) {
        // `not` takes one parenthesized operand; combining it requires grouping.
        if (word(s,0,"not")) return -1;
        Node n; n.kind = conjunction ? Kind::And : Kind::Or;
        n.left = expression(s.substr(0,first),depth+1);
        n.right = expression(s.substr(first+(conjunction ? 3 : 2)),depth+1);
        if (n.left < 0 || n.right < 0) return -1;
        nodes_.push_back(std::move(n)); return static_cast<int>(nodes_.size())-1;
    }
    if (word(s,0,"not")) {
        Node n; n.kind = Kind::Not; n.left = expression(s.substr(3),depth+1);
        if (n.left < 0) return -1;
        nodes_.push_back(std::move(n)); return static_cast<int>(nodes_.size())-1;
    }
    if (s.front() == '(' && s.back() == ')') {
        // The outer pair must enclose the whole expression.
        nesting = 0;
        for (size_t i=0; i+1<s.size(); ++i) {
            if (s[i]=='(') ++nesting;
            else if (s[i]==')' && --nesting==0) return -1;
        }
        auto inner = trim(s.substr(1,s.size()-2));
        if (inner.empty()) return -1;
        if (inner.front() == '(' || word(inner,0,"not")) return expression(inner,depth+1);
        return feature(inner);
    }
    // Unknown functions (including unimplemented style/scroll queries) are
    // general-enclosed. They never become true merely by being negated.
    if (s.find('(') != std::string_view::npos && s.back() == ')') {
        nodes_.emplace_back(); return static_cast<int>(nodes_.size())-1;
    }
    return -1;
}

int ContainerSizeQuery::feature(std::string_view s) {
    Node n; n.kind = Kind::Feature;
    auto name = trim(s);
    std::string_view value;
    auto colon = s.find(':');
    if (colon != std::string_view::npos) {
        name = trim(s.substr(0,colon)); value = trim(s.substr(colon+1));
        if (value.empty()) return -1;
        n.op = Op::Equal;
        if (name.substr(0,4) == "min-") { name.remove_prefix(4); n.op = Op::GreaterEqual; }
        else if (name.substr(0,4) == "max-") { name.remove_prefix(4); n.op = Op::LessEqual; }
    } else {
        size_t at = s.find_first_of("<>=");
        if (at != std::string_view::npos) {
            size_t end = at+1;
            if (end<s.size() && s[end]=='=' && s[at]!='=') ++end;
            const auto op = s.substr(at,end-at);
            name = trim(s.substr(0,at)); value = trim(s.substr(end));
            n.op = op=="<" ? Op::Less : op=="<=" ? Op::LessEqual : op==">" ? Op::Greater : op==">=" ? Op::GreaterEqual : Op::Equal;
            // A two-sided range is two comparisons with a common feature.
            const auto second = value.find_first_of("<>=");
            if (second != std::string_view::npos) {
                if (op=="=" || value[second]=='=' || value[second]!=op.front()) return -1;
                size_t after=second+1;
                if (after<value.size() && value[after]=='=') ++after;
                if (value.find_first_of("<>=",after)!=std::string_view::npos) return -1;
                const auto middle=trim(value.substr(0,second));
                const int a = feature(std::string(name)+std::string(op)+std::string(middle));
                const int b = feature(value);
                if (a<0 || b<0) return -1;
                Node both; both.kind=Kind::And; both.left=a; both.right=b;
                nodes_.push_back(std::move(both)); return static_cast<int>(nodes_.size())-1;
            }
            const auto is_feature = [](std::string_view v) {
                return v=="width" || v=="height" || v=="inline-size" || v=="block-size" || v=="aspect-ratio";
            };
            if (!is_feature(name) && is_feature(value)) {
                std::swap(name,value);
                n.op = n.op==Op::Less ? Op::Greater : n.op==Op::LessEqual ? Op::GreaterEqual :
                    n.op==Op::Greater ? Op::Less : n.op==Op::GreaterEqual ? Op::LessEqual : n.op;
            }
        }
    }
    if (name=="width") { n.feature=Feature::Width; axes_|=1; }
    else if (name=="height") { n.feature=Feature::Height; axes_|=2; }
    else if (name=="inline-size") { n.feature=Feature::InlineSize; axes_|=4; }
    else if (name=="block-size") { n.feature=Feature::BlockSize; axes_|=8; }
    else if (name=="aspect-ratio") { n.feature=Feature::Ratio; axes_|=3; }
    else if (name=="orientation") { n.feature=Feature::Orientation; axes_|=3; }
    else n.kind=Kind::Unknown;
    if (n.kind==Kind::Feature && n.op!=Op::Boolean) {
        if (n.feature==Feature::Orientation) {
            if (n.op!=Op::Equal || (value!="portrait" && value!="landscape")) n.kind=Kind::Unknown;
            n.landscape=value=="landscape";
        } else if (n.feature==Feature::Ratio) {
            const auto slash=value.find('/');
            double numerator=0, denominator=1;
            if (!number(value.substr(0,slash),&numerator) ||
                (slash!=std::string_view::npos && !number(value.substr(slash+1),&denominator)) ||
                numerator<0 || denominator<=0) n.kind=Kind::Unknown;
            else n.ratio=numerator/denominator;
        } else {
            CssParseError error;
            n.value=parse_css_value(value,&error);
            if (!n.value || (n.value->kind()!=CssValueKind::Length && n.value->kind()!=CssValueKind::Calc &&
                !(n.value->kind()==CssValueKind::Number && static_cast<const CssNumber&>(*n.value).value==0)))
                n.kind=Kind::Unknown;
            if (n.value && n.value->kind()==CssValueKind::Calc) {
                const auto& calc=static_cast<const CssCalc&>(*n.value);
                if (!calc.expression || calc_classify(*calc.expression)!=CalcType::Length)
                    n.kind=Kind::Unknown;
            }
        }
    }
    nodes_.push_back(std::move(n)); return static_cast<int>(nodes_.size())-1;
}

uint8_t ContainerSizeQuery::required_axes(bool vertical) const {
    return (axes_&3) | ((axes_&4) ? (vertical ? 2 : 1) : 0) | ((axes_&8) ? (vertical ? 1 : 2) : 0);
}

QueryTruth ContainerSizeQuery::evaluate(const ContainerSizeContext& context) const {
    return root_<0 ? QueryTruth::Unknown : evaluate_node(root_,context);
}

QueryTruth ContainerSizeQuery::evaluate_node(int index, const ContainerSizeContext& c) const {
    const Node& n=nodes_[index];
    if (n.kind==Kind::Unknown) return QueryTruth::Unknown;
    if (n.kind==Kind::Not) return invert(evaluate_node(n.left,c));
    if (n.kind==Kind::And || n.kind==Kind::Or) {
        const auto a=evaluate_node(n.left,c), b=evaluate_node(n.right,c);
        if (n.kind==Kind::And) {
            if (a==QueryTruth::False || b==QueryTruth::False) return QueryTruth::False;
            if (a==QueryTruth::True && b==QueryTruth::True) return QueryTruth::True;
        } else {
            if (a==QueryTruth::True || b==QueryTruth::True) return QueryTruth::True;
            if (a==QueryTruth::False && b==QueryTruth::False) return QueryTruth::False;
        }
        return QueryTruth::Unknown;
    }
    double actual=0, expected=0;
    switch(n.feature) {
        case Feature::Width: actual=c.width; break;
        case Feature::Height: actual=c.height; break;
        case Feature::InlineSize: actual=c.vertical ? c.height : c.width; break;
        case Feature::BlockSize: actual=c.vertical ? c.width : c.height; break;
        case Feature::Ratio: actual=c.height==0 ? INFINITY : c.width/c.height; expected=n.ratio; break;
        case Feature::Orientation:
            return n.op==Op::Boolean || ((c.width>c.height)==n.landscape) ? QueryTruth::True : QueryTruth::False;
    }
    if (n.op==Op::Boolean) return actual!=0 ? QueryTruth::True : QueryTruth::False;
    if (n.value) {
        auto lengths=c.lengths;
        lengths.has_basis=false; // Size query values never accept percentages.
        if (n.value->kind()==CssValueKind::Length) {
            if (!static_cast<const CssLength&>(*n.value).to_pixels(lengths,&expected)) return QueryTruth::Unknown;
        } else if (n.value->kind()==CssValueKind::Calc) {
            if (!static_cast<const CssCalc&>(*n.value).evaluate(lengths,&expected,nullptr)) return QueryTruth::Unknown;
        }
    }
    bool result=false;
    switch(n.op) {
        case Op::Equal: result=actual==expected; break;
        case Op::Less: result=actual<expected; break;
        case Op::LessEqual: result=actual<=expected; break;
        case Op::Greater: result=actual>expected; break;
        case Op::GreaterEqual: result=actual>=expected; break;
        case Op::Boolean: break;
    }
    return result ? QueryTruth::True : QueryTruth::False;
}
} // namespace weva

#pragma once
#include "weva/css_value.h"
#include <cstdint>
#include <string_view>
#include <vector>

namespace weva {

enum class QueryTruth { False, True, Unknown };

struct ContainerSizeContext {
    double width = 0, height = 0; // Content box, before transforms.
    bool vertical = false;
    LengthContext lengths; // Font units use the container; viewport units do not.
};

// Parsed once when a stylesheet is compiled. Normal size comparisons do not
// parse or allocate during evaluation. Unknown features preserve three-valued
// logic, including under not.
class ContainerSizeQuery {
public:
    bool parse(std::string_view condition);
    QueryTruth evaluate(const ContainerSizeContext& context) const;
    // Physical axes required to select a suitable ancestor: 1 = width, 2 = height.
    uint8_t required_axes(bool vertical) const;
private:
    enum class Kind { Unknown, And, Or, Not, Feature };
    enum class Feature { Width, Height, InlineSize, BlockSize, Ratio, Orientation };
    enum class Op { Boolean, Equal, Less, LessEqual, Greater, GreaterEqual };
    struct Node {
        Kind kind = Kind::Unknown;
        Feature feature = Feature::Width;
        Op op = Op::Boolean;
        int left = -1, right = -1;
        CssValuePtr value;
        double ratio = 0;
        bool landscape = false;
    };
    int expression(std::string_view text, unsigned depth);
    int feature(std::string_view text);
    QueryTruth evaluate_node(int index, const ContainerSizeContext& context) const;
    std::vector<Node> nodes_;
    int root_ = -1;
    uint8_t axes_ = 0; // width, height, inline, block
};

} // namespace weva

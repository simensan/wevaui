add_library(weva STATIC
    src/arena.cpp
    src/intern.cpp
    src/geometry.cpp
    src/color.cpp
    src/dom.cpp
    src/html_tables.cpp
    src/html_tokenizer.cpp
    src/html_parser.cpp
    src/css_tokenizer.cpp
    src/css_parser.cpp
    src/css_value.cpp
    src/css_color.cpp
    src/css_calc.cpp
    src/selector_parser.cpp
    src/selector_matcher.cpp
    src/css_properties.cpp
    src/computed_style.cpp
    src/cascade.cpp
    src/variable_resolver.cpp
    src/media.cpp
    src/env_attr.cpp
    src/logical.cpp
    src/at_property.cpp
    src/keyword_resolver.cpp
    src/box.cpp
    src/box_builder.cpp
    src/style_resolver.cpp
    src/block_layout.cpp
    src/shorthand.cpp
    src/user_agent_stylesheet.cpp
    src/font_metrics.cpp
    src/inline_layout.cpp
)
target_include_directories(weva PUBLIC include)
target_link_libraries(weva PUBLIC weva_flags)

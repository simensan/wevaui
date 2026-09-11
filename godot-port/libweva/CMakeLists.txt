# Ada needs C++20. Isolate it so the core and public headers retain C++17.
add_library(weva_url OBJECT ../third_party/ada/ada.cpp src/url_validation.cpp)
target_include_directories(weva_url PRIVATE include)
target_link_libraries(weva_url PRIVATE weva_flags)
target_compile_features(weva_url PRIVATE cxx_std_20)
target_compile_definitions(weva_url PRIVATE ADA_INCLUDE_URL_PATTERN=0)
set_target_properties(weva_url PROPERTIES POSITION_INDEPENDENT_CODE ON)

add_library(weva STATIC
    $<TARGET_OBJECTS:weva_url>
    src/arena.cpp
    src/intern.cpp
    src/geometry.cpp
    src/layout_dump.cpp
    src/grapheme.cpp
    src/color.cpp
    src/dom.cpp
    src/form_state.cpp
    src/temporal_value.cpp
    ../third_party/decimal/decimal.cpp
    src/html_tables.cpp
    src/html_tokenizer.cpp
    src/hit_test.cpp
    src/html_parser.cpp
    src/css_tokenizer.cpp
    src/css_parser.cpp
    src/css_value.cpp
    src/css_color.cpp
    src/color_space.cpp
    src/sticky.cpp
    src/at_import.cpp
    src/css_calc.cpp
    src/selector_parser.cpp
    src/selector_matcher.cpp
    src/css_properties.cpp
    src/computed_style.cpp
    src/cascade.cpp
    src/components.cpp
    src/variable_resolver.cpp
    src/media.cpp
    src/container_query.cpp
    src/container_query_state.cpp
    src/env_attr.cpp
    src/logical.cpp
    src/animation.cpp
    src/at_property.cpp
    src/keyword_resolver.cpp
    src/binding.cpp
    src/box.cpp
    src/box_builder.cpp
    src/style_resolver.cpp
    src/block_layout.cpp
    src/shorthand.cpp
    src/user_agent_stylesheet.cpp
    src/font_metrics.cpp
    src/image_decode.cpp
    src/image_store.cpp
    src/border_image.cpp
    src/inline_layout.cpp
    src/invalidation.cpp
    src/incremental_layout.cpp
    src/keyframes.cpp
    src/anchor.cpp
    src/positioning.cpp
    src/scrollbar.cpp
    src/text_classes.cpp
    src/typeahead.cpp
    src/tessellate.cpp
    src/paint.cpp
    src/background.cpp
    src/software_renderer.cpp
    src/flex.cpp
    src/grid.cpp
    src/table_layout.cpp
    src/multicol.cpp
    src/font_interface.cpp
    src/glyph_atlas.cpp
    src/weva_c.cpp
    src/diagnostic_cycles.cpp
)
target_include_directories(weva PUBLIC include)
target_link_libraries(weva PUBLIC weva_flags PRIVATE weva_icu_i18n)

# Opt-in frame pointers for tools/weva_bench --profile. Off by default so the
# shipped library is not slowed by a diagnostic.
option(WEVA_PROFILE_FRAMES "Keep frame pointers so allocation profiling can walk the stack" OFF)
if(WEVA_PROFILE_FRAMES)
    target_compile_options(weva PRIVATE -fno-omit-frame-pointer)
endif()

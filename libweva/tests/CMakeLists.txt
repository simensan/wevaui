add_executable(weva_tests
    main.cpp
    test_arena.cpp
    test_text_classes.cpp
    test_intern.cpp
    test_geometry.cpp
    test_grapheme.cpp
    test_dom.cpp
    test_form_state.cpp
    test_select_controls.cpp
    test_select_autoscroll.cpp
    test_text_autoscroll.cpp
    test_typeahead.cpp
    test_html.cpp
    test_html_parser.cpp
    test_css_tokenizer.cpp
    test_css_parser.cpp
    test_css_value.cpp
    test_css_calc.cpp
    test_selector.cpp
    test_selector_match.cpp
    test_cascade_store.cpp
    test_cascade.cpp
    test_components.cpp
    test_variables.cpp
    test_media.cpp
    test_pseudo.cpp
    test_cache.cpp
    test_env_attr.cpp
    test_logical.cpp
    test_anchor.cpp
    test_animation.cpp
    test_keyframes.cpp
    test_at_property.cpp
    test_css_properties.cpp
    test_keywords.cpp
    test_box.cpp
    test_box_builder.cpp
    test_style_resolver.cpp
    test_block_layout.cpp
    test_shorthand.cpp
    test_block_flow.cpp
    test_floats.cpp
    test_inline_layout.cpp
    test_positioning.cpp
    test_flex.cpp
    test_grid.cpp
    test_containment.cpp
    test_multicol.cpp
    test_paint.cpp
    test_software_renderer.cpp
    test_text_render.cpp
    test_c_abi.cpp
    test_c_abi_ime.cpp
    test_c_abi_text_editing.cpp
    test_c_abi_maxlength.cpp
    test_c_abi_events.cpp
    test_c_abi_forms.cpp
    test_c_abi_dom.cpp
    test_c_abi_binding.cpp
    test_c_abi_scroll.cpp
    test_c_abi_incremental.cpp
    test_c_abi_keyboard.cpp
    test_c_abi_input_repeat.cpp
    test_c_abi_text_props.cpp
    test_c_abi_interaction.cpp
    test_c_abi_transitions.cpp
    test_table.cpp
    test_background.cpp
    test_image_decode.cpp
    test_focus_move.cpp
    test_backdrop_filter.cpp
)
target_link_libraries(weva_tests PRIVATE weva)
add_test(NAME weva_tests COMMAND weva_tests)

add_executable(weva_style_lookup_allocations test_style_lookup_allocations.cpp)
target_link_libraries(weva_style_lookup_allocations PRIVATE weva)
add_test(NAME weva_style_lookup_allocations COMMAND weva_style_lookup_allocations)
add_executable(weva_container_query_allocations test_container_query_allocations.cpp)
target_link_libraries(weva_container_query_allocations PRIVATE weva)
add_test(NAME weva_container_query_allocations COMMAND weva_container_query_allocations)

add_executable(weva_blur_variants test_blur_variants.cpp blur_portable_reference.cpp)
target_link_libraries(weva_blur_variants PRIVATE weva)
add_test(NAME weva_blur_variants COMMAND weva_blur_variants)

add_executable(weva_geometry_allocations test_geometry_allocations.cpp)
target_link_libraries(weva_geometry_allocations PRIVATE weva)
add_test(NAME weva_geometry_allocations COMMAND weva_geometry_allocations)

add_executable(weva_paint_order_allocations test_paint_order_allocations.cpp)
target_link_libraries(weva_paint_order_allocations PRIVATE weva)
add_test(NAME weva_paint_order_allocations COMMAND weva_paint_order_allocations)

add_executable(weva_radius_allocations test_radius_allocations.cpp)
target_link_libraries(weva_radius_allocations PRIVATE weva)
add_test(NAME weva_radius_allocations COMMAND weva_radius_allocations)

add_executable(weva_clip_allocations test_clip_allocations.cpp)
target_link_libraries(weva_clip_allocations PRIVATE weva)
add_test(NAME weva_clip_allocations COMMAND weva_clip_allocations)

add_executable(weva_draw_mesh_allocations test_draw_mesh_allocations.cpp)
target_link_libraries(weva_draw_mesh_allocations PRIVATE weva)
add_test(NAME weva_draw_mesh_allocations COMMAND weva_draw_mesh_allocations)

add_executable(weva_cascade_allocations test_cascade_allocations.cpp)
target_link_libraries(weva_cascade_allocations PRIVATE weva)
add_test(NAME weva_cascade_allocations COMMAND weva_cascade_allocations)

add_executable(weva_binding_allocations test_binding_allocations.cpp)
target_link_libraries(weva_binding_allocations PRIVATE weva)
add_test(NAME weva_binding_allocations COMMAND weva_binding_allocations)

# Manual diagnostic, not an allocation acceptance gate.
add_executable(weva_update_allocations test_update_allocations.cpp)
target_link_libraries(weva_update_allocations PRIVATE weva)
if(WIN32)
    target_link_libraries(weva_update_allocations PRIVATE dbghelp)
endif()
if(MSVC)
    target_link_options(weva_update_allocations PRIVATE /DEBUG)
endif()

add_executable(weva_css_scalar_allocations test_css_scalar_allocations.cpp)
target_link_libraries(weva_css_scalar_allocations PRIVATE weva)
add_test(NAME weva_css_scalar_allocations COMMAND weva_css_scalar_allocations)

add_executable(weva_binding_path_allocations test_binding_path_allocations.cpp)
target_link_libraries(weva_binding_path_allocations PRIVATE weva)
add_test(NAME weva_binding_path_allocations COMMAND weva_binding_path_allocations)

// Compile the production portable path beside the normal build so SIMD
// machines exercise it too. Rename external definitions, including the
// kernel template, to avoid linking either implementation into the other.
#define WEVA_BLUR_FORCE_PORTABLE
#define parse_gradient portable_parse_gradient
#define resolve_background_layers portable_resolve_background_layers
#define background_texture_detail portable_background_texture_detail
#define background_size_independent portable_background_size_independent
#define rasterize_background portable_rasterize_background
#define rasterize_background_padded portable_rasterize_background_padded
#define rounded_rect_coverage portable_rounded_rect_coverage
#define blur_rgba portable_blur_rgba
#define blur_flat_rgba portable_blur_flat_rgba
#define sample_gradient portable_sample_gradient
#define sample_image portable_sample_image
#define blur_impl portable_blur_impl
#define blur_planes portable_blur_planes
#include "../src/background.cpp"

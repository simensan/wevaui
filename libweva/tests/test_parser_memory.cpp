#include "weva_c.h"
#include "tracked_allocations.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

using weva::test_allocations::live;
static int checks = 0, failures = 0;
static void check(bool passed, const char* description) {
    ++checks;
    if (!passed) { ++failures; std::printf("FAIL %s\n", description); }
}

enum class Replacement { Load, Reload, Fragment };
static void churn(Replacement mode, const char* label) {
    weva_config config{};
    config.viewport_width = 320;
    config.viewport_height = 200;
    auto doc = weva_document_create(&config);
    check(doc != nullptr, "create parser lifetime document");
    if (!doc) std::abort();
    const std::string initial = "<main id=root><div id=item>initial</div></main>";
    check(weva_document_load_html(doc, initial.data(), initial.size()) == WEVA_OK, "load initial tree");
    check(weva_document_update(doc, 0) == WEVA_OK, "publish initial tree");
    auto root = weva_document_query(doc, "#root");
    auto item = weva_document_query(doc, "#item");
    size_t settled = 0;
    for (int iteration = 0; iteration < 512; ++iteration) {
        const std::string suffix = std::to_string(iteration) + std::string(64, 'x');
        std::string attributes;
        for (int index = 0; index < 32; ++index)
            attributes += " data-" + std::to_string(index) + "-" + suffix + "='retained-value'";
        const std::string tag = "x-" + suffix;
        const std::string fragment = "<div id=item" + attributes + "><" + tag + ">payload</" + tag + "></div>";
        const std::string page = "<main id=root>" + fragment + "</main>";
        weva_status status;
        if (mode == Replacement::Load) status = weva_document_load_html(doc, page.data(), page.size());
        else if (mode == Replacement::Reload) status = weva_document_reload_html(doc, page.data(), page.size());
        else status = weva_element_set_html(doc, root, fragment.data(), fragment.size());
        check(status == WEVA_OK, "replace markup with unique parser names");
        check(weva_document_update(doc, 0) == WEVA_OK, "publish replacement");
        const auto current_root = weva_document_query(doc, "#root");
        const auto current_item = weva_document_query(doc, "#item");
        check(current_root != WEVA_ELEMENT_NONE && current_item != WEVA_ELEMENT_NONE, "DOM survives parser lifetime");
        char value[32]{};
        const std::string name = "data-31-" + suffix;
        const auto length = weva_element_attribute(doc, current_item, name.c_str(), value, sizeof(value));
        check(length == std::strlen("retained-value") && std::strcmp(value, "retained-value") == 0,
              "long attribute name and value survive parse completion");
        check(weva_document_query(doc, tag.c_str()) != WEVA_ELEMENT_NONE,
              "custom tag name survives parse completion");
        if (mode == Replacement::Reload) {
            check(root == current_root && item == current_item, "reconciled keyed handles survive");
        } else {
            check(item != current_item, "replaced child handles are not recycled");
            if (mode == Replacement::Fragment) check(root == current_root, "fragment owner survives");
        }
        root = current_root;
        item = current_item;
        if (iteration == 63) settled = live.load();
    }
    const size_t after = live.load();
    check(after <= settled + 64 * 1024, "obsolete parser symbols do not accumulate across replacements");
    std::printf("parser %s: settled %zu bytes, after 512 replacements %zu bytes, added %zu bytes\n",
                label, settled, after, after >= settled ? after - settled : 0);
    weva_document_destroy(doc);
}

int main() {
    churn(Replacement::Load, "load");
    churn(Replacement::Reload, "reload");
    churn(Replacement::Fragment, "fragment");
    std::printf("parser memory: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}

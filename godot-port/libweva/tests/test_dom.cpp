#include "check.h"
#include "weva/dom.h"
#include <string>
#include <vector>

using namespace weva;

static Ref<Element> el(const char* tag) { return make_ref<Element>(tag); }

void test_dom() {
    // ---- tree building + owner document propagation
    auto doc = make_ref<Document>();
    auto root = el("div");
    auto child = el("span");
    CHECK(ok(root->append_child(child.get())));
    CHECK(ok(doc->append_child(root.get())));
    CHECK(child->owner_document() == doc.get());   // propagates into the subtree
    CHECK(child->parent() == root.get());

    // ---- cycle guards return, not throw
    CHECK(root->append_child(root.get()) == Status::OutOfRange);
    CHECK(child->append_child(root.get()) == Status::OutOfRange);
    CHECK(root->append_child(nullptr) == Status::NotFound);

    // ---- versions are monotonic and bump on mutation
    int64_t v = root->version();
    auto second = el("p");
    root->append_child(second.get());
    CHECK(root->version() > v);

    // Re-appending the existing last child is a no-op, including the version.
    v = root->version();
    CHECK(ok(root->append_child(second.get())));
    CHECK(root->version() == v);

    // But moving a non-last child to the end is a real mutation.
    CHECK(ok(root->append_child(child.get())));
    CHECK(root->version() > v);
    CHECK(root->children()[1].get() == child.get());

    // ---- insert_before
    auto first = el("h1");
    CHECK(ok(root->insert_before(first.get(), second.get())));
    CHECK(root->children()[0].get() == first.get());
    CHECK(root->insert_before(first.get(), nullptr) == Status::Ok);  // -> append
    CHECK(root->children()[2].get() == first.get());

    // ---- mutations bubble to ancestors, target stays the mutated node
    std::vector<std::string> seen;
    Node* target = nullptr;
    doc->add_observer([&](const DomMutation& m) {
        seen.push_back(m.name);
        target = m.target;
    });
    child->set_attribute("data-x", "1");
    CHECK(seen.size() == 1 && seen[0] == "data-x");
    CHECK(target == child.get());          // not root, not doc

    // ---- attributes: canonicalised, no-op writes are silent
    child->set_attribute("DATA-Y", "2");
    CHECK(child->get_attribute("data-y") == "2");
    CHECK(child->has_attribute("Data-Y"));
    std::size_t before = seen.size();
    child->set_attribute("data-y", "2");   // identical value
    CHECK(seen.size() == before);
    CHECK(child->remove_attribute("data-y"));
    CHECK(!child->has_attribute("data-y"));
    CHECK(!child->remove_attribute("data-y"));

    // ---- class list + token matching
    child->set_attribute("class", "  alpha\tbeta \n gamma ");
    auto cl = child->class_list();
    CHECK(cl.size() == 3 && cl[0] == "alpha" && cl[2] == "gamma");
    CHECK(has_class_token(child->class_name(), "beta"));
    CHECK(!has_class_token(child->class_name(), "bet"));     // prefix must not match
    CHECK(!has_class_token(child->class_name(), "alphabeta"));

    // ---- queries
    child->set_attribute("id", "target");
    CHECK(doc->get_element_by_id("target") == child.get());
    CHECK(doc->get_element_by_id("missing") == nullptr);
    CHECK(doc->get_elements_by_tag_name("SPAN").size() == 1);
    CHECK(doc->get_elements_by_class_name("beta").size() == 1);

    // ---- text nodes
    auto text = make_ref<TextNode>("hello");
    child->append_child(text.get());
    int64_t tv = text->version();
    text->set_data("hello");                // no-op
    CHECK(text->version() == tv);
    text->set_data("world");
    CHECK(text->version() > tv);
    CHECK(text->data() == "world");
    CHECK(text->source() == "hello");       // parse-time source survives

    // ---- removal: fires before unlinking, detaches owner document
    Node* removed_target = nullptr;
    Node* parent_at_fire = nullptr;
    root->add_observer([&](const DomMutation& m) {
        if (m.kind == MutationKind::ChildRemoved) {
            removed_target = m.related;
            parent_at_fire = m.related->parent();   // must still be linked
        }
    });
    CHECK(root->remove_child(child.get()));
    CHECK(removed_target == child.get());
    CHECK(parent_at_fire == root.get());
    CHECK(child->parent() == nullptr);
    CHECK(child->owner_document() == nullptr);
    CHECK(text->owner_document() == nullptr);       // whole subtree detaches
    CHECK(!root->remove_child(child.get()));

    // The detached subtree is still alive because the test holds a Ref.
    CHECK(child->children().size() == 1);

    // ---- reattaching re-propagates the destination document
    CHECK(ok(doc->append_child(child.get())));
    CHECK(child->owner_document() == doc.get());
    CHECK(text->owner_document() == doc.get());
}

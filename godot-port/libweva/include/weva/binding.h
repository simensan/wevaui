#pragma once
#include "weva/dom.h"

#include <map>
#include <string>
#include <string_view>
#include <vector>

// `{{ path }}` in the markup, filled in from the host's data.
//
// The Unity engine binds a document to a controller object: `{{ Player.Name }}`
// in text or in an attribute, `data-class-<name>="Path"` for a boolean class.
// A host here has no C# objects to reflect over, so the document asks for a
// path and is handed text -- which is all the substitution ever needed, and
// keeps Godot's Variant out of the core entirely.
//
// The markup is the template and stays the template: a TextNode keeps its
// parse-time source, and an attribute's is remembered the first time it is
// filled in, so the same node can be refilled every time the data moves
// without the first substitution eating the braces.

namespace weva {

class BindingResolver {
public:
    virtual ~BindingResolver() = default;
    // Fills `out` with the value at `path`. False means the host does not know
    // the path, and the binding is left showing nothing rather than guessing.
    virtual bool resolve(std::string_view path, std::string* out) const = 0;
    // How many items are in the list at `path`, or -1 when it is not a list.
    // Only `data-each` asks, and a host with no lists can leave it alone.
    virtual int count(std::string_view path) const { (void)path; return -1; }
};

// Where an attribute's template is kept once its value has been filled in.
using BindingTemplates = std::map<const Element*, std::map<std::string, std::string>>;

// True when the text has a `{{` in it at all -- the cheap test that keeps this
// off the hot path for the overwhelming majority of nodes.
bool has_binding(std::string_view text);

// Substitutes every `{{ path }}`. An unknown path becomes empty text, which is
// what a missing value looks like in every template language worth copying.
std::string substitute_bindings(std::string_view text, const BindingResolver& resolver);

// What a `<template data-each>` has produced, so a refresh can tell a list that
// merely changed its values from one whose ITEMS changed. Keyed on the
// template element; the keys are `data-key`'s value per row, or the index when
// there is no `data-key`.
using BindingRepeats = std::map<const Element*, std::vector<std::string>>;

// Applies every binding under `root`: `data-each` repeats, text nodes,
// attribute values, and `data-class-<name>` toggles. Returns how many nodes it
// changed, so a caller can tell a refresh that did something from one that did
// not.
int apply_bindings(Node& root, const BindingResolver& resolver, BindingTemplates* templates,
                   BindingRepeats* repeats = nullptr);

}   // namespace weva

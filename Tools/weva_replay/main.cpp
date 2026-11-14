// weva_replay: play a recorded host session back through the C ABI.
//
// A host (the Agentic engine's `AGENTIC_WEVA_RECORD`) writes one line per call
// it makes into a document: loads, binding writes, list lengths, direct
// element writes, pointer moves, hit tests and updates. This plays the same
// calls, in the same order, against the same markup -- natively, where a
// profiler can see inside the core -- and reports where the time went.
//
//   weva_replay <recording> [--frames=N] [--skip=N] [--root=<dir>]
//
// Lines are tab separated; `\t`, `\n` and `\\` in values are escapes:
//
//   load <html> <css> <base>      view <w> <h>       visible <0|1>
//   bind <path> <value>           list <path> <n>
//   pointer <x> <y> <buttons> <modifiers>
//   accepts <x> <y>               element <x> <y>
//   update <dt>
//
// A bind whose path starts with `#` is a direct element write, as the engine
// performs it: `#id` sets the inline style, `#id/text` the text and
// `#id/<name>` another attribute; an unchanged value is skipped. Other binds
// land in a binding map the document reads through its binding source, and
// bindings are refreshed before an update that follows a changed value.
//
// Windows paths in the recording (`\\?\E:\x\y`) are mapped to `/mnt/e/x/y` on
// POSIX, or used as they are on Windows. `--root` replaces everything before
// `assets` in a path, for a recording made on another machine.

#include "weva_c.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;

double ms_since(Clock::time_point t0) {
    return std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
}

std::vector<std::string> split_tabs(const std::string& line) {
    std::vector<std::string> out;
    std::string field;
    for (size_t i = 0; i < line.size(); ++i) {
        const char c = line[i];
        if (c == '\t') {
            out.push_back(field);
            field.clear();
        } else if (c == '\\' && i + 1 < line.size()) {
            const char n = line[++i];
            field += n == 't' ? '\t' : n == 'n' ? '\n' : n;
        } else {
            field += c;
        }
    }
    out.push_back(field);
    return out;
}

std::string g_root;

std::string map_path(std::string path) {
    if (path.rfind("\\\\?\\", 0) == 0) path = path.substr(4);
    if (!g_root.empty()) {
        for (const char* marker : {"\\assets\\", "/assets/"}) {
            const size_t at = path.find(marker);
            if (at != std::string::npos) {
                path = g_root + path.substr(at);
                break;
            }
        }
    }
#ifndef _WIN32
    if (path.size() > 2 && path[1] == ':') {
        path = std::string("/mnt/") + static_cast<char>(std::tolower(static_cast<unsigned char>(path[0]))) +
               path.substr(2);
    }
    std::replace(path.begin(), path.end(), '\\', '/');
#endif
    return path;
}

bool read_file(const std::string& path, std::string* out) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return false;
    std::ostringstream text;
    text << in.rdbuf();
    *out = text.str();
    return true;
}

struct Host {
    std::unordered_map<std::string, std::string> values;
    std::unordered_map<std::string, int> lists;
    std::unordered_map<std::string, std::string> direct_values;
    std::unordered_map<std::string, weva_element_t> direct_elements;
    bool data_changed = false;
    bool visible = true;
};

size_t binding_value(void* user, const char* path, char* buffer, size_t capacity, int* found) {
    auto* host = static_cast<Host*>(user);
    const auto it = host->values.find(path);
    if (it == host->values.end()) {
        *found = 0;
        return 0;
    }
    *found = 1;
    const std::string& v = it->second;
    if (capacity > 0) {
        const size_t n = std::min(v.size(), capacity - 1);
        std::memcpy(buffer, v.data(), n);
        buffer[n] = '\0';
    }
    return v.size();
}

int binding_count(void* user, const char* path) {
    auto* host = static_cast<Host*>(user);
    const auto it = host->lists.find(path);
    return it == host->lists.end() ? -1 : it->second;
}

struct Totals {
    double refresh = 0, update = 0, input = 0, writes = 0;
    long updates = 0, refreshes = 0, hits = 0, binds = 0, direct = 0;
};

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: weva_replay <recording> [--frames=N] [--skip=N] [--root=<dir>]\n");
        return 2;
    }
    long frames_limit = -1, skip = 0;
    for (int i = 2; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg.rfind("--frames=", 0) == 0) frames_limit = std::atol(arg.c_str() + 9);
        else if (arg.rfind("--skip=", 0) == 0) skip = std::atol(arg.c_str() + 7);
        else if (arg.rfind("--root=", 0) == 0) g_root = arg.substr(7);
    }
    std::ifstream in(argv[1], std::ios::binary);
    if (!in) {
        std::fprintf(stderr, "weva_replay: cannot read %s\n", argv[1]);
        return 1;
    }

    weva_config cfg{};
    cfg.viewport_width = 1920;
    cfg.viewport_height = 1080;
    cfg.use_user_agent_stylesheet = 1;
    weva_document_t doc = weva_document_create(&cfg);
    Host host;
    weva_binding_source source{};
    source.user = &host;
    source.value = binding_value;
    source.count = binding_count;
    weva_document_set_binding_source(doc, &source);

    Totals total;
    long frame = 0;
    bool measuring = skip == 0;
    std::string line;
    std::vector<char> scratch;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        const auto f = split_tabs(line);
        const std::string& op = f[0];
        if (op == "load" && f.size() >= 4) {
            std::string html, css;
            if (!read_file(map_path(f[1]), &html)) {
                std::fprintf(stderr, "weva_replay: cannot read %s\n", map_path(f[1]).c_str());
                return 1;
            }
            if (!f[2].empty()) read_file(map_path(f[2]), &css);
            weva_document_set_base_path(doc, map_path(f[3]).c_str());
            weva_document_set_css(doc, css.data(), css.size());
            weva_document_load_html(doc, html.data(), html.size());
            host.direct_values.clear();
            host.direct_elements.clear();
            host.data_changed = true;
        } else if (op == "view" && f.size() >= 3) {
            weva_document_set_viewport(doc, std::atoi(f[1].c_str()), std::atoi(f[2].c_str()));
        } else if (op == "visible" && f.size() >= 2) {
            host.visible = f[1] == "1";
            if (!host.visible) weva_document_clear_pointer(doc);
        } else if (op == "bind" && f.size() >= 3) {
            const auto t0 = Clock::now();
            const std::string& path = f[1];
            const std::string& value = f[2];
            if (!path.empty() && path[0] == '#') {
                auto& last = host.direct_values[path];
                if (last != value || last.empty()) {
                    const std::string target = path.substr(1);
                    const size_t slash = target.find('/');
                    const std::string id = target.substr(0, slash);
                    const std::string field = slash == std::string::npos ? "style" : target.substr(slash + 1);
                    auto found = host.direct_elements.find(id);
                    if (found == host.direct_elements.end())
                        found = host.direct_elements.emplace(id, weva_document_query(doc, ("#" + id).c_str())).first;
                    if (found->second != WEVA_ELEMENT_NONE) {
                        if (field == "text") weva_element_set_text(doc, found->second, value.c_str());
                        else weva_element_set_attribute(doc, found->second, field.c_str(), value.c_str());
                    }
                    last = value;
                    ++total.direct;
                }
            } else {
                auto& slot = host.values[path];
                if (slot != value) {
                    slot = value;
                    host.data_changed = true;
                }
                ++total.binds;
            }
            if (measuring) total.writes += ms_since(t0);
        } else if (op == "list" && f.size() >= 3) {
            const int n = std::max(0, std::atoi(f[2].c_str()));
            auto it = host.lists.find(f[1]);
            if (it == host.lists.end() || it->second != n) {
                host.lists[f[1]] = n;
                host.data_changed = true;
            }
        } else if (op == "pointer" && f.size() >= 5) {
            const auto t0 = Clock::now();
            weva_document_set_pointer_modifiers(doc, std::atof(f[1].c_str()), std::atof(f[2].c_str()),
                                                static_cast<uint32_t>(std::atol(f[3].c_str())),
                                                static_cast<uint32_t>(std::atol(f[4].c_str())));
            if (measuring) total.input += ms_since(t0);
        } else if ((op == "accepts" || op == "element") && f.size() >= 3) {
            const auto t0 = Clock::now();
            const double x = std::atof(f[1].c_str()), y = std::atof(f[2].c_str());
            if (op == "accepts") (void)weva_document_accepts_pointer(doc, x, y);
            else (void)weva_document_element_at(doc, x, y);
            if (measuring) {
                total.input += ms_since(t0);
                ++total.hits;
            }
        } else if (op == "update" && f.size() >= 2) {
            if (!host.visible) continue;
            if (host.data_changed) {
                const auto t0 = Clock::now();
                weva_document_refresh_bindings(doc);
                if (measuring) {
                    total.refresh += ms_since(t0);
                    ++total.refreshes;
                }
                host.data_changed = false;
            }
            const auto t0 = Clock::now();
            weva_document_update(doc, std::atof(f[1].c_str()));
            size_t draws = 0;
            (void)weva_document_draws(doc, &draws);
            if (measuring) {
                total.update += ms_since(t0);
                ++total.updates;
            }
            ++frame;
            if (!measuring && frame >= skip) measuring = true;
            if (frames_limit >= 0 && frame >= skip + frames_limit) break;
        }
    }
    const double n = static_cast<double>(std::max(1L, total.updates));
    std::printf("weva_replay: %ld updates measured (%ld skipped), %ld refreshes, %ld hit tests, "
                "%ld binds, %ld direct writes\n",
                total.updates, skip, total.refreshes, total.hits, total.binds, total.direct);
    std::printf("  per update: update %.4f ms  refresh %.4f ms  input %.4f ms  writes %.4f ms  "
                "total %.4f ms\n",
                total.update / n, total.refresh / n, total.input / n, total.writes / n,
                (total.update + total.refresh + total.input + total.writes) / n);
    weva_document_destroy(doc);
    return 0;
}

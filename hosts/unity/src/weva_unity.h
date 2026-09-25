/* Unity-only additions to libweva's C ABI. Everything a host needs to run a
 * document is in weva_c.h; this header holds what only the Unity plugin
 * exports, and both generators (exports and P/Invoke) read it after weva_c.h.
 */
#ifndef WEVA_UNITY_H
#define WEVA_UNITY_H

#include "weva_c.h"

/* The plugin compiles its own sources with hidden visibility; a function
 * declared here is exported by the .def on Windows and by this attribute
 * elsewhere (the version script can only export what is visible). */
#if defined(_WIN32)
#define WEVA_UNITY_API
#else
#define WEVA_UNITY_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* The native size of a weva_c.h struct by name ("weva_draw"), 0 for an
 * unknown name. The C# side compares its blittable mirror against this so a
 * layout drift between the header and the generated bindings fails a test
 * instead of corrupting a draw list. */
WEVA_UNITY_API size_t weva_unity_sizeof(const char* struct_name);

#ifdef __cplusplus
}
#endif

#endif

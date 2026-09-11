/* Unity-only additions to libweva's C ABI. Everything a host needs to run a
 * document is in weva_c.h; this header holds what only the Unity plugin
 * exports, and both generators (exports and P/Invoke) read it after weva_c.h.
 */
#ifndef WEVA_UNITY_H
#define WEVA_UNITY_H

#include "weva_c.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The native size of a weva_c.h struct by name ("weva_draw"), 0 for an
 * unknown name. The C# side compares its blittable mirror against this so a
 * layout drift between the header and the generated bindings fails a test
 * instead of corrupting a draw list. */
size_t weva_unity_sizeof(const char* struct_name);

#ifdef __cplusplus
}
#endif

#endif

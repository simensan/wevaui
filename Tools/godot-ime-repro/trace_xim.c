// Diagnostic fixture only: logs the isolated test input and can delay protocol polling.
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static void* original(void* library, const char* name) {
    void* (*lookup)(void*, const char*) = dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
    return lookup(library, name);
}
static double now(void) {
    struct timespec time;
    clock_gettime(CLOCK_MONOTONIC, &time);
    return time.tv_sec + time.tv_nsec / 1e9;
}
static Bool (*filter_fn)(XEvent*, Window);
static void (*focus_fn)(XIC);
static void (*unfocus_fn)(XIC);
static int (*utf8_fn)(XIC, XKeyPressedEvent*, char*, int, KeySym*, Status*);
typedef Bool (*Predicate)(Display*, XEvent*, XPointer);
static Bool (*check_fn)(Display*, XEvent*, Predicate, XPointer);
static _Thread_local double queue_after;

Bool XCheckIfEvent(Display* display, XEvent* event, Predicate predicate, XPointer data) {
    if (now() < queue_after) return False;
    if (!check_fn) check_fn = original(RTLD_NEXT, "XCheckIfEvent");
    return check_fn(display, event, predicate, data);
}

Bool XFilterEvent(XEvent* event, Window window) {
    if (!filter_fn) filter_fn = original(RTLD_NEXT, "XFilterEvent");
    const int type = event->type;
    const unsigned key = type == KeyPress || type == KeyRelease ? event->xkey.keycode : 0;
    const int format = type == ClientMessage ? event->xclient.format : 0;
    // Format-32 XIM messages refer to an X property; their first byte is not an opcode.
    const unsigned opcode = format == 8 ? (unsigned char)event->xclient.data.b[0] : 0;
    const Bool filtered = filter_fn(event, window);
    if (type == KeyPress || type == KeyRelease || type == ClientMessage || type == FocusIn || type == FocusOut)
        fprintf(stderr, "XIM_TRACE %.6f tid=%d filter type=%d key=%u format=%d opcode=%u window=%lx result=%d\n",
                now(), gettid(), type, key, format, opcode, event->xany.window, filtered);
    if (type == KeyPress && key == 0 && !filtered) {
        if (getenv("WEVA_XIM_COMMIT_GAP_MS")) {
            queue_after = now() + atof(getenv("WEVA_XIM_COMMIT_GAP_MS")) / 1000.0;
            fprintf(stderr, "XIM_TRACE %.6f delay following protocol until %.6f\n", now(), queue_after);
        }
    }
    return filtered;
}
void XSetICFocus(XIC context) {
    if (!focus_fn) focus_fn = original(RTLD_NEXT, "XSetICFocus");
    fprintf(stderr, "XIM_TRACE %.6f tid=%d focus context=%p\n", now(), gettid(), (void*)context);
    focus_fn(context);
}
void XUnsetICFocus(XIC context) {
    if (!unfocus_fn) unfocus_fn = original(RTLD_NEXT, "XUnsetICFocus");
    fprintf(stderr, "XIM_TRACE %.6f tid=%d unfocus context=%p\n", now(), gettid(), (void*)context);
    unfocus_fn(context);
}
int Xutf8LookupString(XIC context, XKeyPressedEvent* event, char* buffer, int capacity, KeySym* keysym, Status* status) {
    if (!utf8_fn) utf8_fn = original(RTLD_NEXT, "Xutf8LookupString");
    const int size = utf8_fn(context, event, buffer, capacity, keysym, status);
    fprintf(stderr, "XIM_TRACE %.6f tid=%d lookup key=%u status=%d size=%d text=%.*s\n",
            now(), gettid(), event->keycode, *status, size,
            size > 0 && size <= capacity && *status != XBufferOverflow ? size : 0, buffer);
    return size;
}
// Godot's dynamic wrappers resolve symbols against an explicit libX11 handle.
// Intercept that lookup as well as ordinary ELF symbol binding.
void* dlsym(void* library, const char* name) {
    void* symbol = original(library, name);
    if (!strcmp(name, "XFilterEvent")) { filter_fn = symbol; return XFilterEvent; }
    if (!strcmp(name, "XCheckIfEvent")) { check_fn = symbol; return XCheckIfEvent; }
    if (!strcmp(name, "XSetICFocus")) { focus_fn = symbol; return XSetICFocus; }
    if (!strcmp(name, "XUnsetICFocus")) { unfocus_fn = symbol; return XUnsetICFocus; }
    if (!strcmp(name, "Xutf8LookupString")) { utf8_fn = symbol; return Xutf8LookupString; }
    return symbol;
}

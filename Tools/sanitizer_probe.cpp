// Deliberately broken, isolated positive controls for diagnostic builds only.
// CTest requires the expected sanitizer report and a failing process exit.
#include <cstdlib>
#include <cstring>
#include <limits>

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "undefined") == 0) {
        volatile int largest = std::numeric_limits<int>::max();
        volatile int overflow = largest + argc;
        return overflow == 0;
    }
    auto* allocation = static_cast<volatile unsigned char*>(std::malloc(8));
    if (!allocation) return 2;
    allocation[argc + 7] = 42;
    std::free(const_cast<unsigned char*>(allocation));
    return 0;
}

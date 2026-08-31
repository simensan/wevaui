#pragma once
#include <cstdint>
#include <utility>

namespace weva {

// Intrusive refcount for DOM nodes only.
//
// Everything else in the engine is arena-allocated and dies at end of pass
// (docs/CONVENTIONS.md). DOM nodes are the documented exception: a host —
// GDScript holding an Element, or a C# caller through the ABI — can retain one
// across frames, so they need real lifetimes.
//
// Edge direction matters: a parent holds a strong reference to each child, a
// child holds a raw back-pointer to its parent. A well-formed tree therefore
// has no cycles, and dropping the root frees the subtree.
class RefCounted {
public:
    void add_ref() const { ++refs_; }
    void release() const {
        if (--refs_ == 0) delete this;
    }
    int32_t ref_count() const { return refs_; }

protected:
    RefCounted() = default;
    virtual ~RefCounted() = default;
    RefCounted(const RefCounted&) = delete;
    RefCounted& operator=(const RefCounted&) = delete;

private:
    mutable int32_t refs_ = 1;  // constructed owned
};

template <typename T>
class Ref {
public:
    Ref() = default;
    Ref(std::nullptr_t) {}

    // Adopts an existing reference (matching RefCounted's construct-owned rule).
    explicit Ref(T* p) : p_(p) {}

    static Ref retain(T* p) {
        if (p) p->add_ref();
        return Ref(p);
    }

    Ref(const Ref& o) : p_(o.p_) { if (p_) p_->add_ref(); }
    Ref(Ref&& o) noexcept : p_(o.p_) { o.p_ = nullptr; }

    Ref& operator=(const Ref& o) {
        if (this != &o) { if (o.p_) o.p_->add_ref(); reset(); p_ = o.p_; }
        return *this;
    }
    Ref& operator=(Ref&& o) noexcept {
        if (this != &o) { reset(); p_ = o.p_; o.p_ = nullptr; }
        return *this;
    }
    ~Ref() { reset(); }

    void reset() { if (p_) { p_->release(); p_ = nullptr; } }
    T* get() const { return p_; }
    T* operator->() const { return p_; }
    T& operator*() const { return *p_; }
    explicit operator bool() const { return p_ != nullptr; }

    friend bool operator==(const Ref& a, const Ref& b) { return a.p_ == b.p_; }
    friend bool operator!=(const Ref& a, const Ref& b) { return a.p_ != b.p_; }

private:
    T* p_ = nullptr;
};

template <typename T, typename... Args>
Ref<T> make_ref(Args&&... args) {
    return Ref<T>(new T(std::forward<Args>(args)...));
}

} // namespace weva

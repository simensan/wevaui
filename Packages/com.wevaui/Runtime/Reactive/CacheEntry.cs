namespace Weva.Reactive {
    public struct CacheEntry<T> {
        public long InputVersion;
        public T Value;
        public bool IsValid;

        public bool TryGet(long currentVersion, out T value) {
            if (IsValid && InputVersion == currentVersion) {
                value = Value;
                return true;
            }
            value = default;
            return false;
        }

        public void Set(long version, T value) {
            InputVersion = version;
            Value = value;
            IsValid = true;
        }

        public void Invalidate() {
            IsValid = false;
            Value = default;
        }
    }
}

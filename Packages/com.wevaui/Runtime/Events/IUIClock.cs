using System;

namespace Weva.Events {
    public interface IUIClock {
        double NowSeconds { get; }
    }

    public sealed class SystemUIClock : IUIClock {
        readonly DateTime start = DateTime.UtcNow;
        public double NowSeconds => (DateTime.UtcNow - start).TotalSeconds;
    }

    public sealed class FakeUIClock : IUIClock {
        public double NowSeconds { get; private set; }

        public FakeUIClock(double initial = 0) {
            NowSeconds = initial;
        }

        public void Advance(double seconds) {
            NowSeconds += seconds;
        }

        public void Set(double seconds) {
            NowSeconds = seconds;
        }
    }
}

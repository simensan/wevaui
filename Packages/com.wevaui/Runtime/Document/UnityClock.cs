using UnityEngine;
using Weva.Events;

namespace Weva.Documents {
    // IUIClock backed by Time.unscaledTime. Used by WevaDocument so animations
    // tick consistently with the engine's wall clock and keep running when
    // game time is paused. Tests construct CssAnimationRunner with a
    // FakeUIClock instead.
    //
    // Note: Time is part of the always-available UnityEngine surface; the
    // file is unconditional. The asmdef gates URP-specific code via
    // WEVA_URP — we don't need a guard here.
    public sealed class UnityClock : IUIClock {
        public double NowSeconds => Time.unscaledTimeAsDouble;
    }
}

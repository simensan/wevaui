using System.Collections.Generic;
using Weva.Animation;

namespace Weva.Css.Animation {
    public sealed class RunningAnimationRecord {
        public string Name;
        public KeyframeAnimation Anim;
        public AnimationSpec Spec;
        public AnimationInstance Instance;
        public double StartTimeSeconds;
        // The most recent sample produced by Tick — null if outside active window
        // and no fill applies.
        public Dictionary<string, string> CurrentSample;
        // Used to suspend time advancement when animation-play-state: paused.
        public bool Paused;
        public double PausedAtSeconds;
    }
}

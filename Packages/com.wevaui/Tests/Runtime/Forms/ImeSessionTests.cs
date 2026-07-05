using NUnit.Framework;
using Weva.Forms.Ime;

namespace Weva.Tests.Forms {
    public class ImeSessionTests {
        [Test]
        public void BeginComposition_transitions_to_Composing() {
            var s = new ImeSession();
            Assert.That(s.State, Is.EqualTo(ImeState.Inactive));
            s.BeginComposition();
            Assert.That(s.State, Is.EqualTo(ImeState.Composing));
        }

        [Test]
        public void Updates_while_composing_fire_OnCompositionUpdate() {
            var s = new ImeSession();
            string lastUpdate = null;
            int count = 0;
            s.CompositionUpdated += t => { lastUpdate = t; count++; };
            s.BeginComposition();
            s.UpdateCompositionString("a");
            s.UpdateCompositionString("ab");
            Assert.That(count, Is.EqualTo(2));
            Assert.That(lastUpdate, Is.EqualTo("ab"));
            Assert.That(s.CompositionString, Is.EqualTo("ab"));
        }

        [Test]
        public void Commit_fires_OnCompositionCommit_and_returns_to_Inactive() {
            var s = new ImeSession();
            string committed = null;
            s.CompositionCommitted += t => committed = t;
            s.BeginComposition();
            s.UpdateCompositionString("ab");
            s.CommitComposition("abc");
            Assert.That(committed, Is.EqualTo("abc"));
            Assert.That(s.State, Is.EqualTo(ImeState.Inactive));
            Assert.That(s.CompositionString, Is.EqualTo(""));
        }

        [Test]
        public void Cancel_returns_to_Inactive_without_commit() {
            var s = new ImeSession();
            int commits = 0;
            int cancels = 0;
            s.CompositionCommitted += _ => commits++;
            s.CompositionCancelled += () => cancels++;
            s.BeginComposition();
            s.UpdateCompositionString("ab");
            s.CancelComposition();
            Assert.That(commits, Is.EqualTo(0));
            Assert.That(cancels, Is.EqualTo(1));
            Assert.That(s.State, Is.EqualTo(ImeState.Inactive));
        }

        [Test]
        public void BeginComposition_while_already_composing_replaces_session() {
            var s = new ImeSession();
            int starts = 0;
            s.CompositionStarted += _ => starts++;
            s.BeginComposition();
            s.UpdateCompositionString("ab");
            s.BeginComposition();
            Assert.That(starts, Is.EqualTo(2));
            Assert.That(s.CompositionString, Is.EqualTo(""));
        }

        [Test]
        public void Empty_commit_does_not_fire_committed_event() {
            var s = new ImeSession();
            int commits = 0;
            int cancels = 0;
            s.CompositionCommitted += _ => commits++;
            s.CompositionCancelled += () => cancels++;
            s.BeginComposition();
            s.UpdateCompositionString("ab");
            s.CommitComposition("");
            Assert.That(commits, Is.EqualTo(0));
            Assert.That(cancels, Is.EqualTo(1));
        }

        [Test]
        public void UpdateCompositionString_without_explicit_Begin_starts_composition() {
            var s = new ImeSession();
            int starts = 0;
            s.CompositionStarted += _ => starts++;
            s.UpdateCompositionString("a");
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(s.State, Is.EqualTo(ImeState.Composing));
            Assert.That(s.CompositionString, Is.EqualTo("a"));
        }

        [Test]
        public void Cancel_while_inactive_is_noop() {
            var s = new ImeSession();
            int cancels = 0;
            s.CompositionCancelled += () => cancels++;
            s.CancelComposition();
            Assert.That(cancels, Is.EqualTo(0));
            Assert.That(s.State, Is.EqualTo(ImeState.Inactive));
        }

        [Test]
        public void CompositionCaret_tracks_string_length() {
            var s = new ImeSession();
            s.BeginComposition();
            s.UpdateCompositionString("hello");
            Assert.That(s.CompositionCaret, Is.EqualTo(5));
            s.UpdateCompositionString("hi");
            Assert.That(s.CompositionCaret, Is.EqualTo(2));
        }
    }
}

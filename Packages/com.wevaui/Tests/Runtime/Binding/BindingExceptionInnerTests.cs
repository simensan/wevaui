using System;
using NUnit.Framework;
using Weva.Binding;

namespace Weva.Tests.Binding {
    public class BindingExceptionInnerTests {
        [Test]
        public void Constructor_with_inner_exposes_inner_via_InnerException() {
            var inner = new InvalidOperationException("synthetic inner failure");
            var ex = new BindingException("outer failure", 3, 7, inner);

            Assert.That(ex.InnerException, Is.SameAs(inner));
            Assert.That(ex.Line, Is.EqualTo(3));
            Assert.That(ex.Column, Is.EqualTo(7));
            Assert.That(ex.Message, Does.Contain("outer failure"));
            Assert.That(ex.Message, Does.Contain("line 3"));
            Assert.That(ex.Message, Does.Contain("col 7"));
        }

        [Test]
        public void Wrapping_preserves_synthetic_inner_with_original_message() {
            // Simulate the catch-and-rethrow shape used inside BindingTemplate: a
            // synthetic inner is caught and rewrapped via the new (msg,line,col,inner)
            // ctor. The outer BindingException must expose the original inner so the
            // stack trace and message are preserved.
            const string innerMsg = "synthetic invalid op";
            BindingException caught = null;
            try {
                try {
                    throw new InvalidOperationException(innerMsg);
                } catch (InvalidOperationException ex) {
                    throw new BindingException(ex.Message, 2, 5, ex);
                }
            } catch (BindingException be) {
                caught = be;
            }

            Assert.That(caught, Is.Not.Null);
            Assert.That(caught.InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(caught.InnerException.Message, Is.EqualTo(innerMsg));
            Assert.That(caught.InnerException.StackTrace, Is.Not.Null.And.Not.Empty,
                "original inner stack trace must be preserved");
        }

        [Test]
        public void BindingTemplate_preserves_inner_BindingException_from_path_parse() {
            // Real-path coverage of BindingTemplate.cs:100 — BindingPath.Parse throws
            // a BindingException for an invalid identifier, and the catch wraps it
            // with positional info while chaining the original via InnerException.
            BindingException caught = null;
            try {
                BindingTemplate.Parse("prefix {{ 1bad }} suffix");
            } catch (BindingException ex) {
                caught = ex;
            }

            Assert.That(caught, Is.Not.Null, "expected BindingException from invalid binding path");
            Assert.That(caught.InnerException, Is.Not.Null,
                "outer BindingException must preserve the inner BindingPath parse failure");
            Assert.That(caught.InnerException, Is.InstanceOf<BindingException>());
            Assert.That(caught.InnerException.Message, Does.Contain("1bad"));
        }
    }
}

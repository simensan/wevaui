using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    // Headless coverage of the ClipStack bookkeeping that backs the stencil clip pass. The
    // backend's stencil-write draws are a thin wrapper around this stack — verifying the
    // ref bookkeeping here gives us most of the coverage without spinning up Unity.
    public class StencilClipStateTests {
        [Test]
        public void Push_increments_stencil_ref() {
            var stack = new ClipStack();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(0));
            stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(1));
            stack.TryPush(new Rect(10, 10, 80, 80), BorderRadii.Zero, Transform2D.Identity);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(2));
        }

        [Test]
        public void Pop_decrements_to_parent_ref() {
            var stack = new ClipStack();
            stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
            stack.TryPush(new Rect(10, 10, 80, 80), BorderRadii.Zero, Transform2D.Identity);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(2));
            stack.TryPop();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(1));
            stack.TryPop();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(0));
        }

        [Test]
        public void Empty_stack_pop_is_no_op() {
            var stack = new ClipStack();
            bool result = stack.TryPop();
            Assert.That(result, Is.False);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(0));
            Assert.That(stack.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Ref_starts_at_zero() {
            var stack = new ClipStack();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(0));
            Assert.That(stack.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Reset_clears_all_state() {
            var stack = new ClipStack();
            stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
            stack.TryPush(new Rect(10, 10, 80, 80), BorderRadii.Zero, Transform2D.Identity);
            stack.Reset();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(0));
            Assert.That(stack.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Push_clamps_at_max_depth() {
            var stack = new ClipStack();
            for (int i = 0; i < ClipStack.MaxDepth; i++) {
                bool ok = stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
                Assert.That(ok, Is.True, $"Push {i} should succeed");
            }
            // One more should fail and not increment.
            bool overflow = stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
            Assert.That(overflow, Is.False);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(ClipStack.MaxDepth));
            Assert.That(stack.Depth, Is.EqualTo(ClipStack.MaxDepth));
        }

        [Test]
        public void Top_returns_most_recent_frame_for_pop() {
            var stack = new ClipStack();
            var bounds = new Rect(5, 6, 70, 80);
            var radii = BorderRadii.Uniform(8);
            stack.TryPush(bounds, radii, Transform2D.Identity);
            var top = stack.Top;
            Assert.That(top.Ref, Is.EqualTo(1));
            Assert.That(top.Bounds.X, Is.EqualTo(5));
            Assert.That(top.Bounds.Width, Is.EqualTo(70));
            Assert.That(top.Radii.TopLeft.XRadius, Is.EqualTo(8));
        }

        [Test]
        public void Push_records_border_radii_in_geometry_encoding() {
            var stack = new ClipStack();
            var radii = new BorderRadii(
                new CornerRadius(2, 3),
                new CornerRadius(4, 5),
                new CornerRadius(6, 7),
                new CornerRadius(8, 9));
            stack.TryPush(new Rect(0, 0, 100, 100), radii, Transform2D.Identity);

            var top = stack.Top;
            Assert.That(top.Radii.TopLeft.XRadius, Is.EqualTo(2));
            Assert.That(top.Radii.TopLeft.YRadius, Is.EqualTo(3));
            Assert.That(top.Radii.BottomRight.XRadius, Is.EqualTo(6));
            // Encoding into MeshBuilder via StencilClipGeometry preserves uniform-vs-mixed:
            var builder = new MeshBuilder();
            int i0 = StencilClipGeometry.EncodeClipMask(builder, top.Bounds, top.Radii, Transform2D.Identity);
            Assert.That(i0, Is.EqualTo(0));
            Assert.That(builder.VertexCount, Is.EqualTo(4));
            Assert.That(builder.IndexCount, Is.EqualTo(6));
            // The largest radii pair should be packed into the vertex tangent (PackUniform).
            // Per RoundRectSdf.PackUniform: max(2,4,6,8)=8 and max(3,5,7,9)=9 across corners.
            Assert.That(builder.Vertices[0].Tx, Is.EqualTo(8f));
            Assert.That(builder.Vertices[0].Ty, Is.EqualTo(9f));
        }

        [Test]
        public void Encode_clip_mask_returns_mesh_for_pop_redraw() {
            // The Pop path needs to re-issue the SAME geometry that was pushed so DecrSat
            // covers exactly the fragments IncrSat touched. We verify two encodings produce
            // identical vertex data given the same inputs.
            var radii = BorderRadii.Uniform(6);
            var bounds = new Rect(10, 20, 50, 30);
            var b1 = new MeshBuilder();
            var b2 = new MeshBuilder();
            StencilClipGeometry.EncodeClipMask(b1, bounds, radii, Transform2D.Identity);
            StencilClipGeometry.EncodeClipMask(b2, bounds, radii, Transform2D.Identity);
            Assert.That(b1.VertexCount, Is.EqualTo(b2.VertexCount));
            for (int i = 0; i < b1.VertexCount; i++) {
                Assert.That(b1.Vertices[i].Equals(b2.Vertices[i]), Is.True, $"vertex {i} differs");
            }
        }

        [Test]
        public void Mesh_can_be_cleared_between_push_and_pop_independently_of_stack() {
            // The backend uses a *separate* MeshBuilder for stencil writes (so content draws
            // already in flight don't get muddled with the clip mask). We validate that the
            // stack's bookkeeping doesn't depend on any builder state.
            var stack = new ClipStack();
            var stencilBuilder = new MeshBuilder();
            var bounds = new Rect(0, 0, 100, 100);
            stack.TryPush(bounds, BorderRadii.Zero, Transform2D.Identity);
            StencilClipGeometry.EncodeClipMask(stencilBuilder, bounds, BorderRadii.Zero, Transform2D.Identity);
            Assert.That(stencilBuilder.VertexCount, Is.EqualTo(4));
            stencilBuilder.Reset();
            Assert.That(stencilBuilder.VertexCount, Is.EqualTo(0));
            // Stack still has the frame:
            Assert.That(stack.Depth, Is.EqualTo(1));
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(1));
        }

        [Test]
        public void Nested_pop_restores_nested_ref_correctly() {
            var stack = new ClipStack();
            stack.TryPush(new Rect(0, 0, 100, 100), BorderRadii.Zero, Transform2D.Identity);
            stack.TryPush(new Rect(10, 10, 80, 80), BorderRadii.Zero, Transform2D.Identity);
            stack.TryPush(new Rect(20, 20, 60, 60), BorderRadii.Zero, Transform2D.Identity);
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(3));
            stack.TryPop();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(2));
            stack.TryPop();
            Assert.That(stack.CurrentStencilRef, Is.EqualTo(1));
        }

        [Test]
        public void Max_depth_is_exposed_as_constant() {
            // Per docs, 8-bit stencil = 255 max but we reserve one bit for testing.
            Assert.That(ClipStack.MaxDepth, Is.EqualTo(254));
            Assert.That(StencilClipGeometry.MaxStencilRef, Is.EqualTo(254));
        }
    }
}

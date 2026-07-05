using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class MeshBuilderTests {
        const float Eps = 1e-4f;

        [Test]
        public void Empty_builder_has_no_vertices_or_indices() {
            var b = new MeshBuilder();
            Assert.That(b.VertexCount, Is.EqualTo(0));
            Assert.That(b.IndexCount, Is.EqualTo(0));
            Assert.That(b.Vertices, Is.Empty);
            Assert.That(b.Indices, Is.Empty);
        }

        [Test]
        public void AddQuad_emits_4_vertices_and_6_indices() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 10, 20), LinearColor.White);
            Assert.That(b.VertexCount, Is.EqualTo(4));
            Assert.That(b.IndexCount, Is.EqualTo(6));
        }

        [Test]
        public void AddQuad_corner_positions_match_bounds() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(5, 7, 10, 20), LinearColor.White);
            // TL, BL, BR, TR
            Assert.That(b.Vertices[0].Px, Is.EqualTo(5f).Within(Eps));
            Assert.That(b.Vertices[0].Py, Is.EqualTo(7f).Within(Eps));
            Assert.That(b.Vertices[1].Px, Is.EqualTo(5f).Within(Eps));
            Assert.That(b.Vertices[1].Py, Is.EqualTo(27f).Within(Eps));
            Assert.That(b.Vertices[2].Px, Is.EqualTo(15f).Within(Eps));
            Assert.That(b.Vertices[2].Py, Is.EqualTo(27f).Within(Eps));
            Assert.That(b.Vertices[3].Px, Is.EqualTo(15f).Within(Eps));
            Assert.That(b.Vertices[3].Py, Is.EqualTo(7f).Within(Eps));
        }

        [Test]
        public void AddQuad_uvs_span_unit_square() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 10, 10), LinearColor.White);
            Assert.That(b.Vertices[0].Uvx, Is.EqualTo(0f).Within(Eps));
            Assert.That(b.Vertices[0].Uvy, Is.EqualTo(0f).Within(Eps));
            Assert.That(b.Vertices[2].Uvx, Is.EqualTo(1f).Within(Eps));
            Assert.That(b.Vertices[2].Uvy, Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void Vertex_positions_correct_after_transform_applied() {
            var b = new MeshBuilder();
            var t = Transform2D.Translate(100, 50);
            b.AddQuad(new Rect(0, 0, 10, 10), LinearColor.White, MeshBuilder.EffectIdSolid, 0f, 0f, t);
            Assert.That(b.Vertices[0].Px, Is.EqualTo(100f).Within(Eps));
            Assert.That(b.Vertices[0].Py, Is.EqualTo(50f).Within(Eps));
            Assert.That(b.Vertices[2].Px, Is.EqualTo(110f).Within(Eps));
            Assert.That(b.Vertices[2].Py, Is.EqualTo(60f).Within(Eps));
        }

        [Test]
        public void Color_stored_as_linear_color_unchanged() {
            var b = new MeshBuilder();
            var c = new LinearColor(0.25f, 0.5f, 0.75f, 1f);
            b.AddQuad(new Rect(0, 0, 1, 1), c);
            Assert.That(b.Vertices[0].Color, Is.EqualTo(c));
            Assert.That(b.Vertices[2].Color, Is.EqualTo(c));
        }

        [Test]
        public void Multiple_quads_concatenate_vertex_and_index_buffers() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 1, 1), LinearColor.White);
            b.AddQuad(new Rect(10, 10, 1, 1), LinearColor.Black);
            Assert.That(b.VertexCount, Is.EqualTo(8));
            Assert.That(b.IndexCount, Is.EqualTo(12));
        }

        [Test]
        public void Reset_clears_everything() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 1, 1), LinearColor.White);
            b.AddQuad(new Rect(0, 0, 1, 1), LinearColor.White);
            Assert.That(b.VertexCount, Is.EqualTo(8));
            b.Reset();
            Assert.That(b.VertexCount, Is.EqualTo(0));
            Assert.That(b.IndexCount, Is.EqualTo(0));
        }

        [Test]
        public void Index_buffer_references_only_existing_vertices() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 1, 1), LinearColor.White);
            b.AddQuad(new Rect(2, 2, 1, 1), LinearColor.White);
            for (int i = 0; i < b.IndexCount; i++) {
                Assert.That(b.Indices[i], Is.LessThan(b.VertexCount));
                Assert.That(b.Indices[i], Is.GreaterThanOrEqualTo(0));
            }
        }

        [Test]
        public void Index_buffer_winding_is_consistent_for_each_quad() {
            var b = new MeshBuilder();
            int baseIdx = b.AddQuad(new Rect(0, 0, 4, 4), LinearColor.White);
            // Triangles: (0,1,2) and (0,2,3) where 0=TL, 1=BL, 2=BR, 3=TR
            Assert.That(b.Indices[0], Is.EqualTo(baseIdx + 0));
            Assert.That(b.Indices[1], Is.EqualTo(baseIdx + 1));
            Assert.That(b.Indices[2], Is.EqualTo(baseIdx + 2));
            Assert.That(b.Indices[3], Is.EqualTo(baseIdx + 0));
            Assert.That(b.Indices[4], Is.EqualTo(baseIdx + 2));
            Assert.That(b.Indices[5], Is.EqualTo(baseIdx + 3));
        }

        [Test]
        public void Quad_winding_is_ccw_in_css_top_left_coordinates() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 4, 4), LinearColor.White);
            // In CSS top-left coords (+y goes down), CCW geometry has positive signed area
            // when computed with our formula. Both triangles must have the same sign.
            double a1 = MeshBuilder.SignedTriangleArea(
                b.Vertices[0].Px, b.Vertices[0].Py,
                b.Vertices[1].Px, b.Vertices[1].Py,
                b.Vertices[2].Px, b.Vertices[2].Py);
            double a2 = MeshBuilder.SignedTriangleArea(
                b.Vertices[0].Px, b.Vertices[0].Py,
                b.Vertices[2].Px, b.Vertices[2].Py,
                b.Vertices[3].Px, b.Vertices[3].Py);
            Assert.That(a1 * a2, Is.GreaterThan(0), "Both triangles must have the same winding");
        }

        [Test]
        public void Tangent_carries_radius_and_half_extents() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 20, 10), LinearColor.White, MeshBuilder.EffectIdSolid, 4f, 5f, Transform2D.Identity);
            Assert.That(b.Vertices[0].Tx, Is.EqualTo(4f).Within(Eps));
            Assert.That(b.Vertices[0].Ty, Is.EqualTo(5f).Within(Eps));
            Assert.That(b.Vertices[0].Tz, Is.EqualTo(10f).Within(Eps));
            Assert.That(b.Vertices[0].Tw, Is.EqualTo(5f).Within(Eps));
        }

        [Test]
        public void Effect_id_stored_in_position_z() {
            var b = new MeshBuilder();
            b.AddQuad(new Rect(0, 0, 1, 1), LinearColor.White, MeshBuilder.EffectIdGradient, 0f, 0f, Transform2D.Identity);
            for (int i = 0; i < b.VertexCount; i++) {
                Assert.That(b.Vertices[i].Pz, Is.EqualTo(MeshBuilder.EffectIdGradient).Within(Eps));
            }
        }

        [Test]
        public void Textured_quad_assigns_explicit_uvs_per_corner() {
            var b = new MeshBuilder();
            b.AddTexturedQuad(new Rect(0, 0, 1, 1), LinearColor.White, MeshBuilder.EffectIdText,
                (0.1f, 0.2f), (0.3f, 0.4f), (0.5f, 0.6f), (0.7f, 0.8f), Transform2D.Identity);
            Assert.That(b.Vertices[0].Uvx, Is.EqualTo(0.1f).Within(Eps));
            Assert.That(b.Vertices[1].Uvy, Is.EqualTo(0.4f).Within(Eps));
            Assert.That(b.Vertices[2].Uvx, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(b.Vertices[3].Uvy, Is.EqualTo(0.8f).Within(Eps));
        }
    }
}

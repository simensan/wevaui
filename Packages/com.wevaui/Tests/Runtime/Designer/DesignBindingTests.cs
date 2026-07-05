using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for data binding (M7): text / repeat / class / event bindings compile to
    /// the engine's binding markup (<c>{{ }}</c>, <c>data-each</c>/<c>data-key</c>,
    /// <c>data-class-*</c>, <c>on-*</c>), round-trip through the serializer, and are
    /// editable with undo.
    /// </summary>
    public class DesignBindingTests
    {
        static string Html(DesignNode root)
        {
            return new DesignDocument(root).Compile().Html;
        }

        // --- Compilation ---

        [Test]
        public void Text_binding_emits_mustache()
        {
            var n = new DesignNode("hp") { TextColor = "#fff", FontSize = 16 };
            n.Bind().Text = "Player.Health";
            string html = Html(n);
            Assert.That(html, Does.Contain("{{ Player.Health }}"));
        }

        [Test]
        public void Text_binding_styles_apply_like_a_text_node()
        {
            var n = new DesignNode("hp") { TextColor = "#fff", FontSize = 16 };
            n.Bind().Text = "Player.Health";
            string css = new DesignDocument(n).Compile().Css;
            Assert.That(css, Does.Contain("color: #fff"));
            Assert.That(css, Does.Contain("font-size: 16px"));
        }

        [Test]
        public void Repeat_binding_emits_data_each_and_key()
        {
            var card = new DesignNode("Card");
            card.Bind().RepeatEach = "Inventory.Items as item";
            card.Bind().RepeatKey = "item.id";
            string html = Html(card);
            Assert.That(html, Does.Contain("data-each=\"Inventory.Items as item\""));
            Assert.That(html, Does.Contain("data-key=\"item.id\""));
        }

        [Test]
        public void Class_binding_emits_data_class_attribute()
        {
            var n = new DesignNode("panel");
            n.Bind().BindClass("is-hidden", "Menu.IsClosed");
            Assert.That(Html(n), Does.Contain("data-class-is-hidden=\"Menu.IsClosed\""));
        }

        [Test]
        public void Event_binding_emits_on_event_attribute()
        {
            var n = new DesignNode("PlayButton");
            n.Bind().BindEvent("click", "OnPlay");
            Assert.That(Html(n), Does.Contain("on-click=\"OnPlay\""));
        }

        [Test]
        public void Bound_text_with_special_chars_is_escaped()
        {
            var n = new DesignNode("x");
            n.Bind().Text = "a < b";
            Assert.That(Html(n), Does.Contain("{{ a &lt; b }}"));
        }

        [Test]
        public void No_binding_emits_plain_element()
        {
            var n = new DesignNode("plain");
            string html = Html(n);
            Assert.That(html, Does.Not.Contain("data-each"));
            Assert.That(html, Does.Not.Contain("on-"));
            Assert.That(html, Does.Not.Contain("{{"));
        }

        // --- Serializer ---

        [Test]
        public void Bindings_round_trip_through_serializer()
        {
            var root = new DesignNode("list");
            root.Bind().RepeatEach = "Items as it";
            root.Bind().RepeatKey = "it.id";
            root.Bind().BindClass("is-active", "it.selected");
            root.Bind().BindEvent("click", "Select");
            var child = new DesignNode("label");
            child.Bind().Text = "it.name";
            root.Add(child);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(new DesignDocument(root)));
            NodeBinding rb = reloaded.Root.Binding;
            Assert.That(rb.RepeatEach, Is.EqualTo("Items as it"));
            Assert.That(rb.RepeatKey, Is.EqualTo("it.id"));
            Assert.That(rb.Classes["is-active"], Is.EqualTo("it.selected"));
            Assert.That(rb.Events["click"], Is.EqualTo("Select"));
            Assert.That(reloaded.Root.Children[0].Binding.Text, Is.EqualTo("it.name"));

            // Compiled output identical after reload.
            Assert.That(reloaded.Compile().Html, Is.EqualTo(new DesignDocument(root).Compile().Html));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_text_bind_with_undo()
        {
            var root = new DesignNode("hp");
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetTextBind(root, "Player.Health");
            Assert.That(root.Binding.Text, Is.EqualTo("Player.Health"));

            ed.Undo();
            Assert.That(root.Binding, Is.Null);
        }

        [Test]
        public void Editor_sets_repeat_and_event_then_clears_with_undo()
        {
            var root = new DesignNode("card");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetRepeat(root, "Items as it", "it.id");
            ed.BindEvent(root, "click", "Pick");
            Assert.That(root.Binding.RepeatEach, Is.EqualTo("Items as it"));
            Assert.That(root.Binding.Events["click"], Is.EqualTo("Pick"));

            ed.ClearBinding(root);
            Assert.That(root.Binding, Is.Null);

            ed.Undo();
            Assert.That(root.Binding.RepeatEach, Is.EqualTo("Items as it"));
        }
    }
}

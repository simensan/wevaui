using NUnit.Framework;
using Weva.Figma.Mapping;

namespace Weva.Figma.Tests.Mapping
{
    [TestFixture]
    public class NameAnnotationsTests
    {
        [Test]
        public void ExtractsBindingAndKeepsCleanName()
        {
            NameAnnotations a = NameAnnotations.Parse("Player {{ PlayerName }}");
            Assert.That(a.Binding, Is.EqualTo("{{ PlayerName }}"));
            Assert.That(a.CleanName, Is.EqualTo("Player"));
        }

        [Test]
        public void ParsesId()
        {
            NameAnnotations a = NameAnnotations.Parse("Button #play-btn");
            Assert.That(a.Id, Is.EqualTo("play-btn"));
            Assert.That(a.CleanName, Is.EqualTo("Button"));
        }

        [Test]
        public void ParsesTagOverride()
        {
            NameAnnotations a = NameAnnotations.Parse("Box <button>");
            Assert.That(a.Tag, Is.EqualTo("button"));
        }

        [Test]
        public void ParsesEventHook()
        {
            NameAnnotations a = NameAnnotations.Parse("CTA @click=OnPlay");
            Assert.That(a.Events.Count, Is.EqualTo(1));
            Assert.That(a.Events[0].Key, Is.EqualTo("click"));
            Assert.That(a.Events[0].Value, Is.EqualTo("OnPlay"));
        }

        [Test]
        public void ParsesClassToggle()
        {
            NameAnnotations a = NameAnnotations.Parse("Card .selected?IsSelected");
            Assert.That(a.ClassToggles.Count, Is.EqualTo(1));
            Assert.That(a.ClassToggles[0].Key, Is.EqualTo("selected"));
            Assert.That(a.ClassToggles[0].Value, Is.EqualTo("IsSelected"));
        }

        [Test]
        public void ParsesEachWithItemAndKey()
        {
            NameAnnotations a = NameAnnotations.Parse("List *each=Stages:stage:Number");
            Assert.That(a.Each, Is.Not.Null);
            Assert.That(a.Each.Collection, Is.EqualTo("Stages"));
            Assert.That(a.Each.Item, Is.EqualTo("stage"));
            Assert.That(a.Each.Key, Is.EqualTo("Number"));
        }

        [Test]
        public void EachDefaultsItemAlias()
        {
            NameAnnotations a = NameAnnotations.Parse("*each=Items");
            Assert.That(a.Each.Collection, Is.EqualTo("Items"));
            Assert.That(a.Each.Item, Is.EqualTo("item"));
            Assert.That(a.Each.Key, Is.Null);
        }

        [Test]
        public void ParsesMultipleDirectives()
        {
            NameAnnotations a = NameAnnotations.Parse("Play Button <button> @click=OnPlay #play");
            Assert.That(a.Tag, Is.EqualTo("button"));
            Assert.That(a.Id, Is.EqualTo("play"));
            Assert.That(a.Events.Count, Is.EqualTo(1));
            Assert.That(a.CleanName, Is.EqualTo("Play Button"));
            Assert.That(a.HasDirectives, Is.True);
        }

        [Test]
        public void PlainNameHasNoDirectives()
        {
            NameAnnotations a = NameAnnotations.Parse("Stage Card");
            Assert.That(a.HasDirectives, Is.False);
            Assert.That(a.CleanName, Is.EqualTo("Stage Card"));
        }

        [Test]
        public void SigilLikeTokensThatDontMatchStayInName()
        {
            // ".5" and a lone "@" are not valid directives; they remain plain text.
            NameAnnotations a = NameAnnotations.Parse("Scale .5 @");
            Assert.That(a.HasDirectives, Is.False);
            Assert.That(a.CleanName, Is.EqualTo("Scale .5 @"));
        }
    }
}

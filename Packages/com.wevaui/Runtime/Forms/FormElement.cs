using System;
using System.Collections.Generic;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class FormElement {
        public Element Element { get; }

        readonly Dictionary<Element, string> defaultValues = new();
        readonly Dictionary<Element, bool> defaultChecked = new();
        readonly Dictionary<Element, bool> defaultSelected = new();

        public FormElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "form")
                throw new ArgumentException($"FormElement requires a <form> element; got <{element.TagName}>", nameof(element));
            Element = element;
            CaptureDefaults();
        }

        public string Name {
            get => Element.GetAttribute("name") ?? "";
            set => Element.SetAttribute("name", value ?? "");
        }

        public string Action {
            get => Element.GetAttribute("action") ?? "";
            set => Element.SetAttribute("action", value ?? "");
        }

        public string Method {
            get {
                var m = Element.GetAttribute("method");
                return string.IsNullOrEmpty(m) ? "get" : CssStringUtil.ToLowerInvariantOrSame(m);
            }
            set => Element.SetAttribute("method", value ?? "get");
        }

        void CaptureDefaults() {
            foreach (var c in EnumerateControls()) {
                if (c.TagName == "input" || c.TagName == "textarea") {
                    defaultValues[c] = c.GetAttribute("value");
                }
                if (c.TagName == "input") {
                    var t = c.GetAttribute("type");
                    if (t == "checkbox" || t == "radio") {
                        defaultChecked[c] = c.HasAttribute("checked");
                    }
                }
                if (c.TagName == "option") {
                    defaultSelected[c] = c.HasAttribute("selected");
                }
            }
        }

        public IEnumerable<Element> EnumerateControls() {
            return CollectControls(Element);
        }

        static IEnumerable<Element> CollectControls(Node n) {
            foreach (var child in n.Children) {
                if (child is Element e) {
                    switch (e.TagName) {
                        case "input":
                        case "textarea":
                        case "select":
                        case "button":
                        case "option":
                            yield return e;
                            break;
                    }
                    foreach (var nested in CollectControls(e)) yield return nested;
                }
            }
        }

        public Dictionary<string, string> CollectFormData() {
            var data = new Dictionary<string, string>();
            foreach (var c in EnumerateControls()) {
                var name = c.GetAttribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                if (c.HasAttribute("disabled")) continue;
                switch (c.TagName) {
                    case "input": {
                        var type = c.GetAttribute("type");
                        if (type == "checkbox" || type == "radio") {
                            if (!c.HasAttribute("checked")) continue;
                            data[name] = c.GetAttribute("value") ?? "on";
                        } else if (type == "button" || type == "submit" || type == "reset" || type == "image") {
                            // Excluded from default form data unless they triggered submission.
                            continue;
                        } else {
                            data[name] = c.GetAttribute("value") ?? "";
                        }
                        break;
                    }
                    case "textarea":
                        data[name] = new TextAreaElement(c).Value;
                        break;
                    case "select": {
                        var sel = new SelectElement(c);
                        if (sel.Multiple) {
                            // For multi-select: take the first selected for the dictionary, or skip;
                            // a richer multimap API can be added later.
                            var first = sel.SelectedOption;
                            if (first != null) data[name] = first.Value;
                        } else {
                            var s = sel.SelectedOption;
                            data[name] = s == null ? "" : s.Value;
                        }
                        break;
                    }
                    case "button":
                        // Buttons only contribute when they triggered submit; collect via Submit(Element submitter) below.
                        continue;
                    case "option":
                        // Options contribute via their <select>.
                        continue;
                }
            }
            return data;
        }

        public event Action<FormSubmitEvent> Submitted;
        public event Action Reset;

        public bool Submit(Element submitter = null) {
            var data = CollectFormData();
            if (submitter != null) {
                var name = submitter.GetAttribute("name");
                if (!string.IsNullOrEmpty(name)) {
                    var val = submitter.GetAttribute("value") ?? "";
                    data[name] = val;
                }
            }
            var evt = new FormSubmitEvent(this, submitter, data);
            Submitted?.Invoke(evt);
            return !evt.DefaultPrevented;
        }

        public void DoReset() {
            foreach (var c in EnumerateControls()) {
                if (c.TagName == "input" || c.TagName == "textarea") {
                    if (defaultValues.TryGetValue(c, out var dv)) {
                        if (dv == null) c.RemoveAttribute("value");
                        else c.SetAttribute("value", dv);
                    } else {
                        c.RemoveAttribute("value");
                    }
                }
                if (c.TagName == "input") {
                    var t = c.GetAttribute("type");
                    if (t == "checkbox" || t == "radio") {
                        bool dc = defaultChecked.TryGetValue(c, out var v) && v;
                        if (dc) c.SetAttribute("checked", "");
                        else c.RemoveAttribute("checked");
                    }
                }
                if (c.TagName == "option") {
                    bool ds = defaultSelected.TryGetValue(c, out var v) && v;
                    if (ds) c.SetAttribute("selected", "");
                    else c.RemoveAttribute("selected");
                }
            }
            Reset?.Invoke();
        }
    }

    public sealed class FormSubmitEvent {
        public FormElement Form { get; }
        public Element Submitter { get; }
        public Dictionary<string, string> Data { get; }
        public bool DefaultPrevented { get; private set; }

        public FormSubmitEvent(FormElement form, Element submitter, Dictionary<string, string> data) {
            Form = form;
            Submitter = submitter;
            Data = data;
        }

        public void PreventDefault() {
            DefaultPrevented = true;
        }
    }
}

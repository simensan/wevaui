"""Every name cached as a property id must actually be a registered property.

An unregistered name resolves to kCustomPropertyId, and reading a style by
that id returns nothing -- while reading it by NAME finds it among the custom
properties. So caching the id of a shorthand the registry does not know
silently changes what the engine reads. `list-style` did exactly that: it
suppressed nothing, un-hid every marker, and moved four samples' box counts.
"""
import io, os, re, sys

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "libweva", "src") + os.sep
inc = io.open(SRC + "generated/css_properties.inc", encoding="utf-8").read()
registered = set(re.findall(r'\{"([^"]+)", (?:true|false),', inc))

bad = []
for f in sorted(os.listdir(SRC)):
    if not f.endswith(".cpp"):
        continue
    s = io.open(SRC + f, encoding="utf-8").read()
    for name in re.findall(r'id_of\("([^"]+)"\)', s):
        if name not in registered:
            bad.append((f, name))

for f, name in bad:
    print("  %s caches an id for '%s', which is not a registered property" % (f, name))
print("%d cached id(s) name something the registry does not know" % len(bad))
sys.exit(1 if bad else 0)

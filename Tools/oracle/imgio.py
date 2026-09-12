"""PNG and PPM readers, and a PNG writer, in the standard library alone.

The oracle tools carry no third-party dependencies on purpose: they have to run
wherever the tests run, and Pillow is not there. Between them these cover
everything the corpus holds, since Chrome's screenshots are PNG and both
backends write PPM.
"""
import os
import struct
import zlib

# The corpus, relative to this file, so the tools work from any directory.
SAMPLES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus", "samples")
# Where the render comparison leaves its images (see hosts/godot/compare_render.py
# --out-dir); override with WEVA_DIAG.
DIAG = os.environ.get("WEVA_DIAG", os.path.expanduser("~/weva/diag"))


def unfilter(raw, w, h, ch):
    """Undoes the per-scanline PNG filters, returning w*h*3 RGB bytes."""
    stride, pos = w * ch, 0
    out = bytearray(w * h * 3)
    prev = bytearray(stride)
    for y in range(h):
        f = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if f == 1:                                    # Sub
            for x in range(ch, stride):
                line[x] = (line[x] + line[x - ch]) & 0xFF
        elif f == 2:                                  # Up
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif f == 3:                                  # Average
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 0xFF
        elif f == 4:                                  # Paeth
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                c = prev[x - ch] if x >= ch else 0
                b = prev[x]
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if (pa <= pb and pa <= pc)
                                      else (b if pb <= pc else c))) & 0xFF
        for x in range(w):
            out[(y * w + x) * 3:(y * w + x) * 3 + 3] = line[x * ch:x * ch + 3]
        prev = line
    return bytes(out)


def read_png(path):
    """Reads an 8-bit greyscale/RGB/RGBA PNG as (width, height, RGB bytes)."""
    d = open(path, "rb").read()
    if d[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path}: not a PNG")
    i, idat = 8, b""
    w = h = depth = ctype = None
    while i < len(d):
        (length,) = struct.unpack(">I", d[i:i + 4])
        typ = d[i + 4:i + 8]
        if typ == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", d[i + 8:i + 18])
        elif typ == b"IDAT":
            idat += d[i + 8:i + 8 + length]
        elif typ == b"IEND":
            break
        i += 12 + length
    if depth != 8 or ctype not in (0, 2, 4, 6):
        raise ValueError(f"{path}: unsupported PNG (depth {depth}, colour type {ctype})")
    return w, h, unfilter(zlib.decompress(idat), w, h, {0: 1, 2: 3, 4: 2, 6: 4}[ctype])


def read_ppm(path):
    """Reads a binary PPM (P6) as (width, height, RGB bytes)."""
    d = open(path, "rb").read()
    if not d.startswith(b"P6"):
        raise ValueError(f"{path}: not a binary PPM")
    fields, i = [], 2
    while len(fields) < 3:
        while d[i:i + 1].isspace():
            i += 1
        if d[i:i + 1] == b"#":
            while d[i:i + 1] != b"\n":
                i += 1
            continue
        s = i
        while not d[i:i + 1].isspace():
            i += 1
        fields.append(int(d[s:i]))
    i += 1                                            # the byte that ends the header
    w, h, maxval = fields
    if maxval != 255:
        raise ValueError(f"{path}: only 8-bit PPM is supported")
    return w, h, d[i:i + w * h * 3]


def write_png(path, w, h, buf):
    """Writes w*h*3 RGB bytes as an 8-bit RGB PNG."""
    raw = b"".join(b"\x00" + bytes(buf[y * w * 3:(y + 1) * w * 3]) for y in range(h))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9))
                + chunk(b"IEND", b""))

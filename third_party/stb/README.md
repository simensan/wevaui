# stb_image JPEG decoder

Unmodified `stb_image.h` from https://github.com/nothings/stb/tree/013ac3beddff3dbffafd5177e7972067cd2b5083.
Dual MIT/public-domain license; see LICENSE.txt.

Weva compiles only JPEG support, without stdio or global decoder settings.
PNG remains on the existing core decoder. Encoded dimensions and pixel count
are checked before decoding; the core returns the same RGBA bytes to all hosts.

﻿DieFledermaus
=============

A C# library for the Die Fledermaus compression algorithm, which is simply the [DEFLATE](http://en.wikipedia.org/wiki/DEFLATE) stream with metadata and a magic number. The name exists solely to be a bilingual pun. A Die Fledermaus stream contains:

1. The magic number "`mAuS`" (ASCII `6d 41 75 53`, 4 bytes)
2. A signed 64-bit integer in little-endian order, containing the number of bytes in the DEFLATE stream
3. The DEFLATE-compressed data itself
4. A SHA-512 checksum

The library contains one public type, `DieFledermaus.DieFledermausStream`, which provides the same functionality as the [`System.IO.Compression.DeflateStream`](https://msdn.microsoft.com/en-us/library/system.io.compression.deflatestream.aspx) class.
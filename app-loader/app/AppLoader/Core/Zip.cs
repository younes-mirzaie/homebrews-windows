// Reads single small files (the manifests) out of .xap/.appx packages, which are zip files.
// Windows Phone 8.1 Silverlight has no zip or deflate classes, so this has its own: the zip
// central directory and an inflater after zlib's "puff" (RFC 1951).
using System;
using System.IO;
using System.Text;

namespace AppLoader.Core
{
    public static class Zip
    {
        /// <summary>The named entry's bytes (name compared ignoring case), or null if it isn't there.</summary>
        public static byte[] ReadEntry(Stream zip, string name)
        {
            // End of central directory: the last 22..65557 bytes
            long len = zip.Length;
            int tailLen = (int)Math.Min(len, 22 + 65535);
            var tail = new byte[tailLen];
            zip.Seek(len - tailLen, SeekOrigin.Begin);
            ReadFully(zip, tail, 0, tailLen);
            int eocd = -1;
            for (int i = tailLen - 22; i >= 0; i--)
                if (U32(tail, i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("not a zip file");
            long count = U16(tail, eocd + 10);
            long cdSize = U32(tail, eocd + 12), cdOffset = U32(tail, eocd + 16);
            // ZIP64 (every .appx): the real values are in the ZIP64 end record, found through the
            // locator just before the end record
            if (eocd >= 20 && U32(tail, eocd - 20) == 0x07064b50)
            {
                long z64 = U64(tail, eocd - 20 + 8);
                var rec = new byte[56];
                zip.Seek(z64, SeekOrigin.Begin);
                ReadFully(zip, rec, 0, rec.Length);
                if (U32(rec, 0) != 0x06064b50) throw new InvalidDataException("bad zip64 end record");
                count = U64(rec, 32);
                cdSize = U64(rec, 40);
                cdOffset = U64(rec, 48);
            }
            if (cdSize > 16 * 1024 * 1024) throw new InvalidDataException("zip directory too large");
            var cd = new byte[cdSize];
            zip.Seek(cdOffset, SeekOrigin.Begin);
            ReadFully(zip, cd, 0, cd.Length);
            int p = 0;
            for (long e = 0; e < count; e++)
            {
                if (p + 46 > cd.Length || U32(cd, p) != 0x02014b50) throw new InvalidDataException("bad zip directory");
                int method = U16(cd, p + 10);
                long csize = U32(cd, p + 20), usize = U32(cd, p + 24);
                int nlen = U16(cd, p + 28), xlen = U16(cd, p + 30), clen = U16(cd, p + 32);
                long local = U32(cd, p + 42);
                string entry = Encoding.UTF8.GetString(cd, p + 46, nlen).Replace('\\', '/');
                // ZIP64 extra field: the values that are 0xFFFFFFFF above, in this order
                for (int x = p + 46 + nlen, xend = x + xlen; x + 4 <= xend; )
                {
                    int id = U16(cd, x), size = U16(cd, x + 2), at = x + 4;
                    if (id == 1)
                    {
                        if (usize == 0xFFFFFFFF) { usize = U64(cd, at); at += 8; }
                        if (csize == 0xFFFFFFFF) { csize = U64(cd, at); at += 8; }
                        if (local == 0xFFFFFFFF) local = U64(cd, at);
                    }
                    x += 4 + size;
                }
                p += 46 + nlen + xlen + clen;
                if (!string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (usize > 8 * 1024 * 1024) throw new InvalidDataException(name + " is too large");
                var lh = new byte[30];
                zip.Seek(local, SeekOrigin.Begin);
                ReadFully(zip, lh, 0, 30);
                if (U32(lh, 0) != 0x04034b50) throw new InvalidDataException("bad zip entry");
                zip.Seek(local + 30 + U16(lh, 26) + U16(lh, 28), SeekOrigin.Begin);
                var data = new byte[csize];
                ReadFully(zip, data, 0, data.Length);
                if (method == 0) return data;
                if (method == 8) return Inflate(data, (int)usize);
                throw new InvalidDataException(name + ": unsupported compression " + method);
            }
            return null;
        }

        static void ReadFully(Stream s, byte[] b, int o, int n)
        {
            while (n > 0)
            {
                int r = s.Read(b, o, n);
                if (r <= 0) throw new InvalidDataException("unexpected end of file");
                o += r; n -= r;
            }
        }

        static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static long U32(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }
        static long U64(byte[] b, int i) { return U32(b, i) | (U32(b, i + 4) << 32); }

        // ---- inflate ----

        sealed class State
        {
            public byte[] In; public int InPos; public int BitBuf, BitCnt;
            public byte[] Out; public int OutPos;
        }

        sealed class Huffman { public short[] Count = new short[16]; public short[] Symbol; public Huffman(int n) { Symbol = new short[n]; } }

        static readonly short[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly short[] LExt = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly short[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly short[] DExt = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        public static byte[] Inflate(byte[] input, int outSize)
        {
            var s = new State { In = input, Out = new byte[outSize] };
            int last;
            do
            {
                last = Bits(s, 1);
                int type = Bits(s, 2);
                if (type == 0) Stored(s);
                else if (type == 1) Codes(s, FixedLen, FixedDist);
                else if (type == 2) Dynamic(s);
                else throw new InvalidDataException("bad deflate block");
            } while (last == 0);
            if (s.OutPos != outSize) throw new InvalidDataException("inflated size mismatch");
            return s.Out;
        }

        static int Bits(State s, int need)
        {
            int val = s.BitBuf;
            while (s.BitCnt < need)
            {
                if (s.InPos >= s.In.Length) throw new InvalidDataException("deflate data ends early");
                val |= s.In[s.InPos++] << s.BitCnt;
                s.BitCnt += 8;
            }
            s.BitBuf = val >> need;
            s.BitCnt -= need;
            return val & ((1 << need) - 1);
        }

        static void Stored(State s)
        {
            s.BitBuf = 0; s.BitCnt = 0;
            if (s.InPos + 4 > s.In.Length) throw new InvalidDataException("deflate data ends early");
            int len = s.In[s.InPos] | (s.In[s.InPos + 1] << 8);
            s.InPos += 4;
            if (s.InPos + len > s.In.Length || s.OutPos + len > s.Out.Length) throw new InvalidDataException("bad stored block");
            Buffer.BlockCopy(s.In, s.InPos, s.Out, s.OutPos, len);
            s.InPos += len; s.OutPos += len;
        }

        static int Decode(State s, Huffman h)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                code |= Bits(s, 1);
                int count = h.Count[len];
                if (code - count < first) return h.Symbol[index + (code - first)];
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new InvalidDataException("bad deflate code");
        }

        static void Construct(Huffman h, short[] length, int offset, int n)
        {
            Array.Clear(h.Count, 0, 16);
            for (int i = 0; i < n; i++) h.Count[length[offset + i]]++;
            var offs = new short[16];
            for (int len = 1; len < 15; len++) offs[len + 1] = (short)(offs[len] + h.Count[len]);
            for (int i = 0; i < n; i++)
                if (length[offset + i] != 0) h.Symbol[offs[length[offset + i]]++] = (short)i;
        }

        static void Codes(State s, Huffman lencode, Huffman distcode)
        {
            for (;;)
            {
                int symbol = Decode(s, lencode);
                if (symbol < 256)
                {
                    if (s.OutPos >= s.Out.Length) throw new InvalidDataException("inflated data too long");
                    s.Out[s.OutPos++] = (byte)symbol;
                }
                else if (symbol == 256) return;
                else
                {
                    symbol -= 257;
                    if (symbol >= 29) throw new InvalidDataException("bad length code");
                    int len = LBase[symbol] + Bits(s, LExt[symbol]);
                    int ds = Decode(s, distcode);
                    if (ds >= 30) throw new InvalidDataException("bad distance code");
                    int dist = DBase[ds] + Bits(s, DExt[ds]);
                    if (dist > s.OutPos || s.OutPos + len > s.Out.Length) throw new InvalidDataException("bad distance");
                    for (int i = 0; i < len; i++, s.OutPos++) s.Out[s.OutPos] = s.Out[s.OutPos - dist];
                }
            }
        }

        static readonly Huffman FixedLen = new Huffman(288), FixedDist = new Huffman(30);

        static Zip()
        {
            var lengths = new short[288 + 30];
            int i = 0;
            for (; i < 144; i++) lengths[i] = 8;
            for (; i < 256; i++) lengths[i] = 9;
            for (; i < 280; i++) lengths[i] = 7;
            for (; i < 288; i++) lengths[i] = 8;
            Construct(FixedLen, lengths, 0, 288);
            for (i = 0; i < 30; i++) lengths[288 + i] = 5;
            Construct(FixedDist, lengths, 288, 30);
        }

        static void Dynamic(State s)
        {
            int nlen = Bits(s, 5) + 257, ndist = Bits(s, 5) + 1, ncode = Bits(s, 4) + 4;
            if (nlen > 286 || ndist > 30) throw new InvalidDataException("bad dynamic block");
            var lengths = new short[320];
            for (int i = 0; i < ncode; i++) lengths[Order[i]] = (short)Bits(s, 3);
            var lencode = new Huffman(320);
            var distcode = new Huffman(30);
            Construct(lencode, lengths, 0, 19);
            int index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(s, lencode);
                if (symbol < 16) { lengths[index++] = (short)symbol; continue; }
                short len = 0;
                int repeat;
                if (symbol == 16)
                {
                    if (index == 0) throw new InvalidDataException("bad repeat");
                    len = lengths[index - 1];
                    repeat = 3 + Bits(s, 2);
                }
                else if (symbol == 17) repeat = 3 + Bits(s, 3);
                else repeat = 11 + Bits(s, 7);
                if (index + repeat > nlen + ndist) throw new InvalidDataException("bad repeat");
                while (repeat-- > 0) lengths[index++] = len;
            }
            var litlen = new Huffman(288);
            Construct(litlen, lengths, 0, nlen);
            Construct(distcode, lengths, nlen, ndist);
            Codes(s, litlen, distcode);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TransitLab.Services;

/// <summary>
/// Decodes Rice/GZIP tile-compressed FITS images (.fz) per the FITS Tile Compression
/// Convention, as implemented by CFITSIO (imcompress.c / ricecomp.c). Supports RICE_1 and
/// GZIP_1 tiles with BITPIX 8/16/32/-32/-64, and SUBTRACTIVE_DITHER_1/2 float unquantization.
/// HCOMPRESS_1 and PLIO_1 (used for large survey mosaics / pixel masks, not CCD/CMOS transit
/// photometry frames) are not supported and throw NotSupportedException.
/// </summary>
public static class FitsCompressionService
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>True if the file's primary HDU is an empty shell fronting a compressed-image extension.</summary>
    public static bool IsCompressed(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var kv = FitsHeaderService.ReadHeaderBlock(fs);
        if (!(kv.TryGetValue("NAXIS", out var naxis) && naxis == "0" &&
              kv.TryGetValue("EXTEND", out var ext) && ext == "T"))
            return false;

        var extKv = FitsHeaderService.ReadHeaderBlock(fs);
        return extKv.TryGetValue("XTENSION", out var xt) && xt == "BINTABLE" &&
               extKv.TryGetValue("ZIMAGE", out var zi) && zi == "T";
    }

    /// <summary>Decode a Rice/GZIP tile-compressed FITS image into a flat row-major float array.</summary>
    public static FitsImageService.FitsImage Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        FitsHeaderService.ReadHeaderBlock(fs);              // primary HDU (empty shell)
        long extHeaderStart = fs.Position;
        var kv = FitsHeaderService.ReadHeaderBlock(fs);      // BINTABLE extension header
        long tableStart = fs.Position;

        if (!(kv.TryGetValue("XTENSION", out var xt) && xt == "BINTABLE" &&
              kv.TryGetValue("ZIMAGE", out var zi) && zi == "T"))
            throw new InvalidDataException("Not a Rice/GZIP tile-compressed FITS image.");

        int width  = GetInt(kv, "ZNAXIS1");
        int height = GetInt(kv, "ZNAXIS2");
        int zbitpix = GetInt(kv, "ZBITPIX");
        int tileW  = GetIntOr(kv, "ZTILE1", width);
        int tileH  = GetIntOr(kv, "ZTILE2", 1);
        string cmpType = kv.GetValueOrDefault("ZCMPTYPE", "RICE_1").Trim();
        string quantiz = kv.GetValueOrDefault("ZQUANTIZ", "").Trim();
        int ditherSeed = kv.TryGetValue("ZDITHER0", out var dz) && int.TryParse(dz, out var dzv) ? dzv : 1;

        int blockSize = 32, riceBytePix = zbitpix == 0 ? 4 : Math.Max(1, Math.Min(4, Math.Abs(zbitpix) / 8));
        for (int n = 1; kv.TryGetValue($"ZNAME{n}", out var zname); n++)
        {
            if (!kv.TryGetValue($"ZVAL{n}", out var zval) || !int.TryParse(zval, out var zvalInt)) continue;
            if (zname.Trim() == "BLOCKSIZE") blockSize = zvalInt;
            else if (zname.Trim() == "BYTEPIX") riceBytePix = zvalInt;
        }

        double globalBscale = kv.TryGetValue("BSCALE", out var gbs) && NumericParseService.TryParse(gbs, out var gbsv) ? gbsv : 1.0;
        double globalBzero  = kv.TryGetValue("BZERO",  out var gbz) && NumericParseService.TryParse(gbz, out var gbzv) ? gbzv : 0.0;

        int tfields = GetInt(kv, "TFIELDS");
        int rowWidth = GetInt(kv, "NAXIS1");
        int numRows  = GetInt(kv, "NAXIS2");
        long pcount  = kv.TryGetValue("PCOUNT", out var pc) && long.TryParse(pc, out var pcv) ? pcv : 0;
        long theap   = kv.TryGetValue("THEAP", out var th) && long.TryParse(th, out var thv) ? thv : (long)rowWidth * numRows;

        var columns = new List<(string Name, char Type, int Repeat, int ByteWidth, int Offset)>();
        int runningOffset = 0;
        for (int f = 1; f <= tfields; f++)
        {
            string name  = kv.GetValueOrDefault($"TTYPE{f}", "").Trim();
            string tform = kv.GetValueOrDefault($"TFORM{f}", "").Trim();
            var (type, repeat, byteWidth) = ParseTform(tform);
            columns.Add((name, type, repeat, byteWidth, runningOffset));
            runningOffset += byteWidth;
        }

        int compIdx  = FindColumn(columns, "COMPRESSED_DATA");
        int gzipIdx  = FindColumn(columns, "GZIP_COMPRESSED_DATA");
        int zscaleIdx = FindColumn(columns, "ZSCALE");
        int zzeroIdx  = FindColumn(columns, "ZZERO");
        int zblankIdx = FindColumn(columns, "ZBLANK");

        if (compIdx < 0 && gzipIdx < 0)
            throw new InvalidDataException("Compressed FITS extension has no COMPRESSED_DATA column.");

        long heapStart = tableStart + theap;
        var pixels = new float[(long)width * height];

        int numTilesX = (width  + tileW - 1) / tileW;
        int numTilesY = (height + tileH - 1) / tileH;
        var rowBuf = new byte[rowWidth];

        bool isFloat = zbitpix < 0;
        bool dither  = quantiz.StartsWith("SUBTRACTIVE_DITHER");
        bool dither2 = quantiz == "SUBTRACTIVE_DITHER_2";

        for (int row = 1; row <= numRows; row++)
        {
            fs.Seek(tableStart + (long)(row - 1) * rowWidth, SeekOrigin.Begin);
            fs.ReadExactly(rowBuf, 0, rowWidth);

            double scale = globalBscale, zero = globalBzero;
            if (zscaleIdx >= 0) scale = ReadDouble(rowBuf, columns[zscaleIdx].Offset);
            if (zzeroIdx  >= 0) zero  = ReadDouble(rowBuf, columns[zzeroIdx].Offset);

            int tileIndex = row - 1;
            int tx = tileIndex % numTilesX;
            int ty = tileIndex / numTilesX;
            int x0 = tx * tileW, y0 = ty * tileH;
            int tw = Math.Min(tileW, width  - x0);
            int th2 = Math.Min(tileH, height - y0);
            int tileLen = tw * th2;

            float[] tileValues = DecodeTile(
                fs, rowBuf, columns, compIdx, gzipIdx, tileLen,
                cmpType, blockSize, riceBytePix, zbitpix, isFloat, dither, dither2,
                scale, zero, row, ditherSeed, heapStart);

            // Copy tile (row-major within tile) into the full image buffer.
            for (int ry = 0; ry < th2; ry++)
            {
                long destRowStart = (long)(y0 + ry) * width + x0;
                Array.Copy(tileValues, ry * tw, pixels, destRowStart, tw);
            }
        }

        return new FitsImageService.FitsImage(pixels, width, height);
    }

    // ── Tile decode ───────────────────────────────────────────────────────────

    private static float[] DecodeTile(
        FileStream fs, byte[] rowBuf,
        List<(string Name, char Type, int Repeat, int ByteWidth, int Offset)> columns,
        int compIdx, int gzipIdx, int tileLen,
        string cmpType, int blockSize, int riceBytePix, int zbitpix,
        bool isFloat, bool dither, bool dither2,
        double scale, double zero, int row, int ditherSeed, long heapStart)
    {
        byte[]? compBytes = null;
        bool usedGzipFallback = false;

        if (compIdx >= 0)
        {
            var (count, offset) = ReadDescriptor(rowBuf, columns[compIdx].Offset, columns[compIdx].Type);
            if (count > 0)
                compBytes = ReadHeapBytes(fs, heapStart, offset, count);
        }
        if (compBytes == null && gzipIdx >= 0)
        {
            var (count, offset) = ReadDescriptor(rowBuf, columns[gzipIdx].Offset, columns[gzipIdx].Type);
            if (count > 0)
            {
                compBytes = Gunzip(ReadHeapBytes(fs, heapStart, offset, count));
                usedGzipFallback = true;
            }
        }

        if (compBytes == null)
        {
            // Fully degenerate tile (no data in either column) — treat as all-zero.
            return new float[tileLen];
        }

        int[]? intData = null;
        float[]? floatData = null;

        if (usedGzipFallback)
        {
            // GZIP fallback stores the tile's native uncompressed bytes, big-endian, in the
            // tile's native pixel type (raw float bytes if lossless float, else integers).
            if (isFloat && !dither)
                floatData = BytesToFloats(compBytes, zbitpix, tileLen);
            else
                intData = BytesToInts(compBytes, zbitpix, tileLen);
        }
        else if (cmpType == "RICE_1")
        {
            intData = RiceDecompress(compBytes, tileLen, blockSize, riceBytePix);
        }
        else if (cmpType is "GZIP_1" or "GZIP_2")
        {
            var raw = Gunzip(compBytes);
            if (isFloat && !dither)
                floatData = BytesToFloats(raw, zbitpix, tileLen);
            else
                intData = BytesToInts(raw, zbitpix, tileLen);
        }
        else
        {
            throw new NotSupportedException($"Unsupported FITS tile compression algorithm: {cmpType}");
        }

        if (floatData != null) return floatData;

        // intData was assigned in every branch above that didn't return early.
        return isFloat
            ? Unquantize(intData!, scale, zero, row, ditherSeed, dither2)
            : LinearScale(intData!, scale, zero);
    }

    // ── RICE_1 decompression (ported from CFITSIO ricecomp.c, public domain / NASA GSFC) ──

    private static readonly int[] NonzeroCount = BuildNonzeroCountTable();

    private static int[] BuildNonzeroCountTable()
    {
        var t = new int[256];
        for (int i = 1; i < 256; i++)
        {
            int bits = 0, v = i;
            while (v > 0) { bits++; v >>= 1; }
            t[i] = bits;
        }
        return t;
    }

    private static int[] RiceDecompress(byte[] c, int nx, int nblock, int pixelBytes)
    {
        (int fsbits, int fsmax) = pixelBytes switch
        {
            1 => (3, 6),
            2 => (4, 14),
            _ => (5, 25),
        };
        int bbits = 1 << fsbits;
        uint outMask = pixelBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu };

        var array = new uint[nx];
        if (nx == 0) return [];

        int p = 0;
        uint lastpix = 0;
        for (int k = 0; k < pixelBytes; k++) lastpix = (lastpix << 8) | c[p++];

        uint b = c[p++];
        int nbits = 8;
        int i = 0;
        while (i < nx)
        {
            nbits -= fsbits;
            while (nbits < 0) { b = (b << 8) | c[p++]; nbits += 8; }
            int fs = (int)(b >> nbits) - 1;
            b &= (uint)((1 << nbits) - 1);
            int imax = Math.Min(i + nblock, nx);

            if (fs < 0)
            {
                for (; i < imax; i++) array[i] = lastpix;
            }
            else if (fs == fsmax)
            {
                for (; i < imax; i++)
                {
                    int k = bbits - nbits;
                    uint diff = b << k;
                    for (k -= 8; k >= 0; k -= 8)
                    {
                        b = c[p++];
                        diff |= b << k;
                    }
                    if (nbits > 0)
                    {
                        b = c[p++];
                        diff |= b >> (-k);
                        b &= (uint)((1 << nbits) - 1);
                    }
                    else
                    {
                        b = 0;
                    }
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    array[i] = (diff + lastpix) & outMask;
                    lastpix = array[i];
                }
            }
            else
            {
                for (; i < imax; i++)
                {
                    while (b == 0) { nbits += 8; b = c[p++]; }
                    int nzero = nbits - NonzeroCount[b];
                    nbits -= nzero + 1;
                    b ^= (uint)(1 << nbits);
                    nbits -= fs;
                    while (nbits < 0) { b = (b << 8) | c[p++]; nbits += 8; }
                    uint diff = ((uint)nzero << fs) | (b >> nbits);
                    b &= (uint)((1 << nbits) - 1);
                    diff = (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
                    array[i] = (diff + lastpix) & outMask;
                    lastpix = array[i];
                }
            }
        }

        var result = new int[nx];
        for (int j = 0; j < nx; j++) result[j] = unchecked((int)array[j]);
        return result;
    }

    // ── Float unquantization (ported from CFITSIO imcompress.c unquantize_i4r4, public domain / NASA GSFC) ──

    private const int NRandom = 10000;
    private const int ZeroValue = -2147483646;
    private static float[]? _randValues;

    private static float[] RandValues()
    {
        if (_randValues != null) return _randValues;
        var vals = new float[NRandom];
        double a = 16807.0, m = 2147483647.0, seed = 1.0;
        for (int i = 0; i < NRandom; i++)
        {
            double temp = a * seed;
            seed = temp - m * (int)(temp / m);
            vals[i] = (float)(seed / m);
        }
        _randValues = vals;
        return vals;
    }

    private static float[] Unquantize(int[] input, double scale, double zero, int row, int ditherSeed, bool dither2)
    {
        var rv = RandValues();
        int iseed = (int)(((long)row + ditherSeed - 2) % NRandom);
        if (iseed < 0) iseed += NRandom;
        int nextrand = (int)(rv[iseed] * 500);

        var output = new float[input.Length];
        for (int ii = 0; ii < input.Length; ii++)
        {
            output[ii] = dither2 && input[ii] == ZeroValue
                ? 0f
                : (float)((input[ii] - rv[nextrand] + 0.5) * scale + zero);

            nextrand++;
            if (nextrand == NRandom)
            {
                iseed++;
                if (iseed == NRandom) iseed = 0;
                nextrand = (int)(rv[iseed] * 500);
            }
        }
        return output;
    }

    private static float[] LinearScale(int[] input, double scale, double zero)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (float)(scale * input[i] + zero);
        return output;
    }

    // ── BINTABLE / heap helpers ───────────────────────────────────────────────

    private static (char Type, int Repeat, int ByteWidth) ParseTform(string tform)
    {
        int i = 0;
        while (i < tform.Length && char.IsDigit(tform[i])) i++;
        int repeat = i > 0 ? int.Parse(tform[..i]) : 1;
        char type = i < tform.Length ? tform[i] : 'B';
        int unitBytes = type switch
        {
            'L' or 'A' or 'B' => 1,
            'I' => 2,
            'J' or 'E' or 'P' => 4,
            'K' or 'D' or 'Q' => 8,
            'C' => 8,
            'M' => 16,
            _ => 1,
        };
        // 'P'/'Q' are (count,offset) descriptor pairs — always one 8-byte (P) or 16-byte (Q)
        // structure regardless of the leading digit (which historically documents the
        // max array length of the pointed-to heap data, e.g. "1PB(2247)").
        if (type is 'P' or 'Q') return (type, 1, type == 'P' ? 8 : 16);
        return (type, repeat, unitBytes * repeat);
    }

    private static int FindColumn(List<(string Name, char Type, int Repeat, int ByteWidth, int Offset)> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static (int Count, long Offset) ReadDescriptor(byte[] row, int offset, char type)
    {
        if (type == 'Q')
        {
            long count      = ReadInt64BE(row, offset);
            long heapOffset = ReadInt64BE(row, offset + 8);
            return ((int)count, heapOffset);
        }
        int count32 = (row[offset] << 24) | (row[offset + 1] << 16) | (row[offset + 2] << 8) | row[offset + 3];
        int heapOffset32 = (row[offset + 4] << 24) | (row[offset + 5] << 16) | (row[offset + 6] << 8) | row[offset + 7];
        return (count32, heapOffset32);
    }

    private static long ReadInt64BE(byte[] row, int offset)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | row[offset + i];
        return v;
    }

    private static double ReadDouble(byte[] row, int offset)
    {
        Span<byte> b = stackalloc byte[8];
        for (int i = 0; i < 8; i++) b[i] = row[offset + 7 - i];
        return BitConverter.ToDouble(b);
    }

    private static byte[] ReadHeapBytes(FileStream fs, long heapStart, long offset, int count)
    {
        var buf = new byte[count];
        fs.Seek(heapStart + offset, SeekOrigin.Begin);
        fs.ReadExactly(buf, 0, count);
        return buf;
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var src = new MemoryStream(compressed);
        using var gz  = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    private static int[] BytesToInts(byte[] raw, int bitpix, int count)
    {
        var result = new int[count];
        int bpp = Math.Abs(bitpix) / 8;
        for (int i = 0; i < count; i++)
        {
            int o = i * bpp;
            result[i] = bitpix switch
            {
                8  => raw[o],
                16 => (short)((raw[o] << 8) | raw[o + 1]),
                32 => (raw[o] << 24) | (raw[o + 1] << 16) | (raw[o + 2] << 8) | raw[o + 3],
                _  => 0,
            };
        }
        return result;
    }

    private static float[] BytesToFloats(byte[] raw, int bitpix, int count)
    {
        var result = new float[count];
        int bpp = Math.Abs(bitpix) / 8;
        Span<byte> b8 = stackalloc byte[8];

        for (int i = 0; i < count; i++)
        {
            int o = i * bpp;
            if (bitpix == -32)
            {
                var b4 = b8[..4];
                for (int k = 0; k < 4; k++) b4[k] = raw[o + 3 - k];
                result[i] = BitConverter.ToSingle(b4);
            }
            else if (bitpix == -64)
            {
                for (int k = 0; k < 8; k++) b8[k] = raw[o + 7 - k];
                result[i] = (float)BitConverter.ToDouble(b8);
            }
        }
        return result;
    }

    // ── Header parsing helpers ────────────────────────────────────────────────

    private static int GetInt(Dictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : 0;

    private static int GetIntOr(Dictionary<string, string> kv, string key, int fallback) =>
        kv.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
}

using System;
using System.Collections.Generic;
using System.IO;

namespace TransitLab.Services;

/// <summary>Minimal FITS image reader — supports BITPIX 8/16/32/-32/-64 with BZERO/BSCALE.</summary>
public static class FitsImageService
{
    public sealed record FitsImage(float[] Pixels, int Width, int Height);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fast background estimate by row-sampling ~10 000 pixels (25th percentile).</summary>
    public static float EstimateBackground(string path)
    {
        if (FitsCompressionService.IsCompressed(path))
            return (float)EstimateBackgroundFromPixels(FitsCompressionService.Decode(path).Pixels);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = ReadHeader(fs);
        if (meta.Width == 0 || meta.Height == 0) return 0f;

        int w = meta.Width, h = meta.Height;
        int bytesPerPixel = Math.Abs(meta.Bitpix) / 8;
        long dataOffset = fs.Position;   // set by ReadHeader

        int rowStep = Math.Max(1, h / 100);
        int colStep = Math.Max(1, w / 100);
        int bytesPerRow = w * bytesPerPixel;
        var rowBuf = new byte[bytesPerRow];
        var samples = new List<float>(100 * 100);

        for (int row = 0; row < h; row += rowStep)
        {
            long rowOffset = dataOffset + (long)row * bytesPerRow;
            fs.Seek(rowOffset, SeekOrigin.Begin);
            int read = fs.Read(rowBuf, 0, bytesPerRow);
            if (read < bytesPerRow) break;

            for (int col = 0; col < w; col += colStep)
            {
                double rv = ReadPixelValue(rowBuf, col * bytesPerPixel, meta.Bitpix);
                samples.Add((float)(meta.Bzero + meta.Bscale * rv));
            }
        }

        if (samples.Count == 0) return 0f;
        samples.Sort();
        return samples[samples.Count / 4];   // 25th percentile
    }

    /// <summary>Estimate background ADU from a pre-loaded pixel array (25th percentile).</summary>
    public static double EstimateBackgroundFromPixels(float[] pixels)
    {
        var sorted = (float[])pixels.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 4];   // 25th percentile
    }

    /// <summary>Load the full first image plane as float pixels (BZERO/BSCALE applied).</summary>
    public static FitsImage Load(string path)
    {
        if (FitsCompressionService.IsCompressed(path))
            return FitsCompressionService.Decode(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = ReadHeader(fs);
        if (meta.Width == 0 || meta.Height == 0) return new FitsImage([], 0, 0);

        int w = meta.Width, h = meta.Height;
        int bytesPerPixel = Math.Abs(meta.Bitpix) / 8;
        int totalPixels = w * h;
        var raw = new byte[totalPixels * bytesPerPixel];
        fs.ReadExactly(raw);

        var pixels = new float[totalPixels];

        // Fast path: standard unsigned 16-bit (BITPIX=16, BZERO=32768, BSCALE=1).
        // FITS stores the raw int16; physical = BZERO + BSCALE * raw_int16 = raw_int16 + 32768.
        // Reading the two bytes directly as ushort (without sign-extending) gives a value
        // shifted by 32768 — we MUST read as signed short then add BZERO.
        if (meta.Bitpix == 16 && meta.Bzero == 32768.0 && meta.Bscale == 1.0)
        {
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 2;
                short s = (short)((raw[o] << 8) | raw[o + 1]);
                pixels[i] = s + 32768f;   // physical 0–65535
            }
        }
        // Signed 16-bit, no offset
        else if (meta.Bitpix == 16 && meta.Bzero == 0.0 && meta.Bscale == 1.0)
        {
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 2;
                short s = (short)((raw[o] << 8) | raw[o + 1]);
                pixels[i] = s;
            }
        }
        else
        {
            // General path
            for (int i = 0; i < totalPixels; i++)
            {
                double rv = ReadPixelValue(raw, i * bytesPerPixel, meta.Bitpix);
                pixels[i] = (float)(meta.Bzero + meta.Bscale * rv);
            }
        }

        return new FitsImage(pixels, w, h);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private sealed record FitsMeta(int Bitpix, int Width, int Height, double Bzero, double Bscale);

    /// <summary>
    /// Reads FITS header blocks until the END card, populating key keywords.
    /// Leaves the stream positioned at the start of the data (next 2880-byte boundary).
    /// </summary>
    private static FitsMeta ReadHeader(FileStream fs)
    {
        int bitpix = 0, width = 0, height = 0;
        double bzero = 0.0, bscale = 1.0;
        var block = new byte[2880];
        var card  = new byte[80];

        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 2880) break;

            bool foundEnd = false;
            for (int c = 0; c < 36; c++)
            {
                Array.Copy(block, c * 80, card, 0, 80);
                var key = System.Text.Encoding.ASCII.GetString(card, 0, 8);

                if (key.StartsWith("END", StringComparison.Ordinal))
                { foundEnd = true; break; }

                var cardStr = System.Text.Encoding.ASCII.GetString(card, 0, 80);
                var trimKey = key.TrimEnd();

                if      (trimKey == "BITPIX") bitpix = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS1") width  = ParseCardInt(cardStr);
                else if (trimKey == "NAXIS2") height = ParseCardInt(cardStr);
                else if (trimKey == "BZERO")  bzero  = ParseCardDouble(cardStr);
                else if (trimKey == "BSCALE") bscale = ParseCardDouble(cardStr);
            }

            if (foundEnd) break;
        }

        // fs.Position is already at the data start (each iteration reads exactly 2880 bytes)
        return new FitsMeta(bitpix, width, height, bzero, bscale);
    }

    private static int ParseCardInt(string card)
    {
        // Value field is columns 10–79 (before '/')
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return int.TryParse(val, out var v) ? v : 0;
    }

    private static double ParseCardDouble(string card)
    {
        var val = card.Length > 10 ? card[10..].Split('/')[0].Trim() : "";
        return NumericParseService.TryParse(val, out var v) ? v : 0.0;
    }

    private static double ReadPixelValue(byte[] raw, int o, int bitpix) => bitpix switch
    {
        8  => raw[o],
        16 => (short)((raw[o] << 8) | raw[o + 1]),
        32 => (int)(((uint)raw[o] << 24) | ((uint)raw[o + 1] << 16) | ((uint)raw[o + 2] << 8) | raw[o + 3]),
       -32 => ToSingleBE(raw, o),
       -64 => ToDoubleBE(raw, o),
        _  => 0.0,
    };

    private static float ToSingleBE(byte[] raw, int o)
    {
        if (BitConverter.IsLittleEndian)
        {
            byte[] b = [raw[o + 3], raw[o + 2], raw[o + 1], raw[o]];
            return BitConverter.ToSingle(b, 0);
        }
        return BitConverter.ToSingle(raw, o);
    }

    private static double ToDoubleBE(byte[] raw, int o)
    {
        if (BitConverter.IsLittleEndian)
        {
            byte[] b = [raw[o + 7], raw[o + 6], raw[o + 5], raw[o + 4],
                        raw[o + 3], raw[o + 2], raw[o + 1], raw[o]];
            return BitConverter.ToDouble(b, 0);
        }
        return BitConverter.ToDouble(raw, o);
    }
}

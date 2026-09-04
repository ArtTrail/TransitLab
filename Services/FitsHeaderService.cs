using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TransitLab.Services;

/// <summary>
/// Reads keyword values from a FITS primary header without any external library.
/// FITS headers consist of 2880-byte blocks; each 80-byte record has the form:
///   KEYWORD = VALUE / comment
/// </summary>
public static class FitsHeaderService
{
    public class FitsHeader
    {
        private readonly Dictionary<string, string> _kv;
        public FitsHeader(Dictionary<string, string> kv) => _kv = kv;

        /// <summary>Return the raw string value for a keyword, or "" if absent.</summary>
        public string Get(string keyword)
            => _kv.TryGetValue(keyword.ToUpperInvariant().TrimEnd(), out var v) ? v : "";

        /// <summary>Return a double or null if absent / unparseable.</summary>
        public double? GetDouble(string keyword)
            => NumericParseService.TryParse(Get(keyword), out var d)
               ? d : null;

        /// <summary>Return an int or null if absent / unparseable.</summary>
        public int? GetInt(string keyword)
            => int.TryParse(Get(keyword), out var i) ? i : null;
    }

    /// <summary>
    /// Read one HDU's header (2880-byte blocks of 80-byte cards) starting at the stream's
    /// current position. Leaves the stream positioned at the start of that HDU's data
    /// (the next 2880-byte boundary after the END card).
    /// </summary>
    public static Dictionary<string, string> ReadHeaderBlock(FileStream fs)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = new byte[2880];
        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 80) break;

            bool end = false;
            for (int i = 0; i + 79 < read; i += 80)
            {
                var record = Encoding.ASCII.GetString(block, i, 80);
                var kw     = record[..8].TrimEnd();

                if (kw == "END") { end = true; break; }

                // Only value records contain '=' at position 8
                if (record.Length > 9 && record[8] == '=')
                {
                    var rawVal = record[9..].Split('/')[0].Trim();

                    // Strip FITS string quotes
                    if (rawVal.StartsWith('\''))
                    {
                        rawVal = rawVal.Trim('\'').Trim();
                    }

                    kv[kw] = rawVal;
                }
            }
            if (end) break;
        }
        return kv;
    }

    /// <summary>
    /// Read the effective header from a FITS file. Throws on I/O or format error.
    /// For Rice/GZIP tile-compressed images (.fz, primary HDU is an empty shell per the
    /// FITS Tile Compression convention), transparently reads the first extension's header
    /// instead and remaps ZBITPIX/ZNAXIS/ZNAXISn back to BITPIX/NAXIS/NAXISn so callers see
    /// the logical (uncompressed) image header, same as astropy/CFITSIO present it.
    /// </summary>
    public static FitsHeader Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var kv = ReadHeaderBlock(fs);

        bool looksCompressedContainer =
            kv.TryGetValue("NAXIS", out var naxisStr) && naxisStr == "0" &&
            kv.TryGetValue("EXTEND", out var extendStr) && extendStr == "T";

        if (looksCompressedContainer)
        {
            var extKv = ReadHeaderBlock(fs);
            if (extKv.TryGetValue("XTENSION", out var xt) && xt == "BINTABLE" &&
                extKv.TryGetValue("ZIMAGE", out var zimg) && zimg == "T")
            {
                if (extKv.TryGetValue("ZBITPIX", out var zbp)) extKv["BITPIX"] = zbp;
                if (extKv.TryGetValue("ZNAXIS",  out var znx)) extKv["NAXIS"]  = znx;
                for (int n = 1; extKv.TryGetValue($"ZNAXIS{n}", out var znxn); n++)
                    extKv[$"NAXIS{n}"] = znxn;

                return new FitsHeader(extKv);
            }
        }

        return new FitsHeader(kv);
    }

    /// <summary>Find the first FITS file in a directory (alphabetically, case-insensitive).</summary>
    public static string? FindFirstFits(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var ext in new[] { "*.fits", "*.fit", "*.fts", "*.fz" })
        {
            var files = Directory.GetFiles(directory, ext);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length > 0) return files[0];
        }
        return null;
    }
}

using System;
using System.IO;
using Avalonia.Platform;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace TransitLab.Services;

/// <summary>
/// Stamps the TransitLab icon in the upper-left corner of FinalLightCurve
/// PNG and PDF files produced by EXOTIC after each reduction run.
/// </summary>
public static class WatermarkService
{
    private const string IconUri      = "avares://TransitLab/Assets/TransitLab.ico";

    // PNG: icon size as a fraction of image height, clamped to a sensible range
    private const double IconFraction  = 0.067;  // 6.7% of plot height (-20% from 0.084)
    private const int    MinIconPx     = 46;     // was 58  (-20%)
    private const int    MaxIconPx     = 77;     // was 96  (-20%)
    private const int    PngMarginPx   = 4;

    // PDF: icon size and margin in points (72 pt = 1 inch).
    // Kept smaller than the PNG stamp so the icon fits entirely in the
    // whitespace above the Y-axis labels (plot area starts ~50 pt from top).
    private const double PdfIconPts    = 44.0;
    private const double PdfMarginPts  = 2.0;

    /// <summary>
    /// Stamps the TransitLab icon on every FinalLightCurve_*.png and
    /// FinalLightCurve_*.pdf found under <paramref name="saveDir"/>.
    /// Runs synchronously — call from a background thread.
    /// </summary>
    public static void StampLightCurveFiles(string saveDir)
    {
        if (!Directory.Exists(saveDir)) return;

        // ── Load icon ─────────────────────────────────────────────────────────
        SKBitmap? icon = null;
        try
        {
            using var assetStream = AssetLoader.Open(new Uri(IconUri));
            icon = SKBitmap.Decode(assetStream);
            if (icon is null)
            {
                SessionLogService.Write("[Watermark] Icon decode returned null — skipping stamp");
                return;
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Watermark] Could not load icon: {ex.Message}");
            icon?.Dispose();
            return;
        }

        using (icon)
        {
            // PNG stamp: original colours, but the brightest pixels (the light
            // curve arc) are pushed to white so they're visible on the plot.
            using var pngIcon = CreatePngIcon(icon);

            // PDF stamp: full white silhouette (unchanged).
            using var whiteIcon = CreateWhiteIcon(icon);
            using var wms = new MemoryStream();
            whiteIcon.Encode(wms, SKEncodedImageFormat.Png, 100);
            byte[] whiteIconPngBytes = wms.ToArray();

            // ── PNG ───────────────────────────────────────────────────────────
            foreach (var png in Directory.GetFiles(saveDir, "FinalLightCurve_*.png",
                                                   SearchOption.AllDirectories))
            {
                try
                {
                    StampPng(png, pngIcon);
                    SessionLogService.Write($"[Watermark] ✓  {Path.GetFileName(png)}");
                }
                catch (Exception ex)
                {
                    SessionLogService.Write($"[Watermark] ✗  PNG {Path.GetFileName(png)}: {ex.Message}");
                }
            }

            // ── PDF ───────────────────────────────────────────────────────────
            foreach (var pdf in Directory.GetFiles(saveDir, "FinalLightCurve_*.pdf",
                                                   SearchOption.AllDirectories))
            {
                try
                {
                    StampPdf(pdf, whiteIconPngBytes);
                    SessionLogService.Write($"[Watermark] ✓  {Path.GetFileName(pdf)}");
                }
                catch (Exception ex)
                {
                    SessionLogService.Write($"[Watermark] ✗  PDF {Path.GetFileName(pdf)}: {ex.Message}");
                }
            }
        }
    }

    // ── White silhouette ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts the full-colour icon to a white silhouette.
    /// Each output pixel: RGB = white, A = 3 × luminance of the source pixel.
    /// The near-black background (luminance ≈ 0.04 → A ≈ 12%) becomes nearly
    /// transparent; icon graphics (luminance ≥ 0.33) become fully opaque white.
    /// </summary>
    private static SKBitmap CreateWhiteIcon(SKBitmap src)
    {
        var result = new SKBitmap(new SKImageInfo(src.Width, src.Height,
                                                  SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(result);
        // R=G=B=1 (full white); A = 3×(0.2126·R + 0.7152·G + 0.0722·B)
        float[] m = {
            0,       0,       0,       0, 1,          // R = white
            0,       0,       0,       0, 1,          // G = white
            0,       0,       0,       0, 1,          // B = white
            0.6378f, 2.1456f, 0.2166f, 0, 0,         // A = 3 × luminance
        };
        using var cf    = SKColorFilter.CreateColorMatrix(m);
        using var paint = new SKPaint { ColorFilter = cf };
        canvas.DrawBitmap(src, 0, 0, paint);
        return result;
    }

    // ── PNG icon — colour preserved, light curve forced to white ─────────────

    /// <summary>
    /// Produces a version of the icon for PNG stamping: the original colours
    /// are kept intact, but any pixel whose luminance exceeds
    /// <see cref="PngCurveThreshold"/> is converted to pure white (same alpha).
    /// The light curve arc is the brightest element in the icon (lum ≥ ~0.7),
    /// so it becomes white; the dark background and coloured elements are
    /// unaffected (lum typically &lt; 0.35 for the background, 0.1–0.4 for
    /// coloured elements).
    /// </summary>
    private const float PngCurveThreshold = 0.45f;

    private static SKBitmap CreatePngIcon(SKBitmap src)
    {
        var result = new SKBitmap(new SKImageInfo(src.Width, src.Height,
                                                  SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                var   px  = src.GetPixel(x, y);
                float r   = px.Red   / 255f;
                float g   = px.Green / 255f;
                float b   = px.Blue  / 255f;
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                // Bright pixels → white (light curve arc).  Dark/coloured → original.
                result.SetPixel(x, y, lum >= PngCurveThreshold
                    ? new SKColor(255, 255, 255, px.Alpha)
                    : px);
            }
        }
        return result;
    }

    // ── PNG ───────────────────────────────────────────────────────────────────

    private static void StampPng(string pngPath, SKBitmap icon)
    {
        SKBitmap src;
        using (var fs = File.OpenRead(pngPath))
            src = SKBitmap.Decode(fs);

        using (src)
        {
            int iconPx = Math.Clamp((int)(src.Height * IconFraction), MinIconPx, MaxIconPx);

            var info = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // Draw the original light curve
            canvas.DrawBitmap(src, 0, 0);

            // Scale icon and draw at upper-left (~90% opacity).
            // CreateDilate(1,1) thickens thin arc lines at small stamp sizes.
            using var scaled = icon.Resize(new SKImageInfo(iconPx, iconPx), SKFilterQuality.High);
            using var paint  = new SKPaint
            {
                // Opacity only — no colour modulation so the icon's own colours
                // (including the white light curve) are preserved faithfully.
                Color       = new SKColor(255, 255, 255, 230),
                ImageFilter = SKImageFilter.CreateDilate(1, 1),
            };
            canvas.DrawBitmap(scaled, PngMarginPx, PngMarginPx, paint);

            // ── "TransitLab" label — crisp vector text, independent of the icon raster ──
            // Scaled proportionally to the icon (11–14 px) so it's always readable.
            // Drawn twice: dark outline first for contrast on any chart background,
            // then white fill on top.
            float textSize = Math.Clamp(iconPx * 0.15f, 11f, 14f);
            float textX    = PngMarginPx + iconPx * 0.5f;   // centered under icon
            float textY    = PngMarginPx + iconPx + textSize + 1f;

            using var outlinePaint = new SKPaint
            {
                Color       = new SKColor(0, 0, 0, 160),
                TextSize    = textSize,
                IsAntialias = true,
                TextAlign   = SKTextAlign.Center,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f,
                StrokeJoin  = SKStrokeJoin.Round,
            };
            using var labelPaint = new SKPaint
            {
                Color       = SKColors.White.WithAlpha(220),
                TextSize    = textSize,
                IsAntialias = true,
                TextAlign   = SKTextAlign.Center,
            };

            canvas.DrawText("TransitLab", textX, textY, outlinePaint);
            canvas.DrawText("TransitLab", textX, textY, labelPaint);

            // Overwrite the original file
            using var snapshot = surface.Snapshot();
            using var encoded  = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            using var outFs    = new FileStream(pngPath, FileMode.Create, FileAccess.Write);
            encoded.SaveTo(outFs);
        }
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    private static void StampPdf(string pdfPath, byte[] iconPngBytes)
    {
        // Save to a temp file first, then atomically replace — avoids corrupting
        // the original if something fails mid-write.
        var tempPath = pdfPath + ".wm_tmp";
        try
        {
            var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
            try
            {
                var page = doc.Pages[0];
                using var gfx  = XGraphics.FromPdfPage(page);
                // XImage.FromStream requires a stream factory so it can re-open
                // the stream if PdfSharp needs to read the image data more than once.
                var xImg = XImage.FromStream(() => new MemoryStream(iconPngBytes));
                gfx.DrawImage(xImg, PdfMarginPts, PdfMarginPts, PdfIconPts, PdfIconPts);
                doc.Save(tempPath);
            }
            finally
            {
                doc.Close();
            }
            File.Move(tempPath, pdfPath, overwrite: true);
        }
        catch
        {
            // Clean up temp file if anything went wrong before the rename
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }
}

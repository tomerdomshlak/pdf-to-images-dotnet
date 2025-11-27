using ImageMagick;
using Microsoft.AspNetCore.Http;
using PdfToImages.Api.Models;
using PdfToImages.Api.Services.Ocr;

namespace PdfToImages.Api.Services;

public sealed class ImageProcessorMagick : IImageProcessor
{
    private readonly IOcrService _ocrService;
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };
    private static readonly HashSet<string> PngExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png" };
    private static readonly HashSet<string> WebpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".webp" };
    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };
    private static readonly HashSet<string> GifExtensions = new(StringComparer.OrdinalIgnoreCase) { ".gif" };
    private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };
    private static readonly HashSet<string> BmpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".bmp" };

    public ImageProcessorMagick(IOcrService ocrService)
    {
        _ocrService = ocrService;
    }

    public async Task<FileConversionResponse> ProcessFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var processed = await ProcessFileToBlobsAsync(file, cancellationToken);
        var fileResponse = new FileConversionResponse { OriginalFileName = processed.OriginalFileName, Pages = new List<ImagePageResponse>() };
        foreach (var page in processed.Pages)
        {
            var base64 = Convert.ToBase64String(page.Bytes);
            var dataUrl = $"data:{page.MimeType};base64,{base64}";
            var responsePage = new ImagePageResponse
            {
                PageNumber = page.PageNumber,
                MimeType = page.MimeType,
                DataUrl = dataUrl,
                Width = page.Width,
                Height = page.Height,
                SizeBytes = page.Bytes.LongLength
            };
            try
            {
                responsePage.Ocr = await _ocrService.ExtractAsync(page.Bytes, cancellationToken);
            }
            catch
            {
                responsePage.Ocr = null;
            }
            fileResponse.Pages.Add(responsePage);
        }

        return fileResponse;
    }

    public async Task<ProcessedFile> ProcessFileToBlobsAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var originalName = file.FileName;
        var extension = Path.GetExtension(originalName) ?? string.Empty;
        var isPdf = PdfExtensions.Contains(extension);
        var targetFormat = GetTargetFormat(extension, isPdf);

        await using var inputStream = new MemoryStream();
        await file.CopyToAsync(inputStream, cancellationToken);
        inputStream.Position = 0;
        var originalBytes = inputStream.ToArray();

        var result = new ProcessedFile
        {
            OriginalFileName = originalName,
            Pages = new List<ProcessedImage>()
        };

        if (isPdf)
        {
            var readSettings = new MagickReadSettings
            {
                // Increase DPI for higher fidelity on text/lines
                Density = new Density(400, 400),
                BackgroundColor = MagickColors.White
            };

            using var pages = new MagickImageCollection();
            pages.Read(inputStream, readSettings);

            var pageIndex = 0;
            foreach (var page in pages)
            {
                pageIndex++;
                // For PDFs, keep the previous visually safe target logic (PNG default),
                // as comparing against original PDF bytes is not meaningful here.
                var blob = EncodeToTargetBlob(page, targetFormat, pageIndex, Path.GetFileNameWithoutExtension(originalName), /*sourceIsPdf*/ true);
                result.Pages.Add(blob);
            }
        }
        else
        {
            using var collection = new MagickImageCollection();
            // read from a fresh stream for Magick
            using (var fresh = new MemoryStream(originalBytes))
            {
                collection.Read(fresh);
            }

            var frameIndex = 0;
            // Single-frame non-PDF: keep original format and ensure result is strictly smaller
            if (collection.Count == 1)
            {
                var frame = collection[0];
                var baseName = Path.GetFileNameWithoutExtension(originalName);
                // JPEG path: quality ramp then downscale ramp
                if (JpegExtensions.Contains(extension))
                {
                    foreach (var q in new[] { 90, 85, 80, 75, 70, 65, 60 })
                    {
                        var (jpegBytes, jpegWidth, jpegHeight) = EncodeJpeg(frame, q);
                        if (jpegBytes.LongLength < (long)originalBytes.LongLength)
                        {
                            result.Pages.Add(new ProcessedImage
                            {
                                PageNumber = 1,
                                MimeType = "image/jpeg",
                                FileExtension = ".jpg",
                                SuggestedFileName = $"{baseName}-page-001.jpg",
                                Width = jpegWidth,
                                Height = jpegHeight,
                                Bytes = jpegBytes
                            });
                            return result;
                        }
                    }
                    foreach (var pct in new[] { 95, 90, 85, 80, 75 })
                    {
                        using var resized = frame.Clone();
                        resized.FilterType = FilterType.Lanczos;
                        resized.Resize(new Percentage(pct));
                        foreach (var q in new[] { 85, 80, 75, 70, 65 })
                        {
                            var (jpegBytes, jpegWidth, jpegHeight) = EncodeJpeg(resized, q);
                            if (jpegBytes.LongLength < (long)originalBytes.LongLength)
                            {
                                result.Pages.Add(new ProcessedImage
                                {
                                    PageNumber = 1,
                                    MimeType = "image/jpeg",
                                    FileExtension = ".jpg",
                                    SuggestedFileName = $"{baseName}-page-001.jpg",
                                    Width = jpegWidth,
                                    Height = jpegHeight,
                                    Bytes = jpegBytes
                                });
                                return result;
                            }
                        }
                    }
                    // Fallback: keep original but still preserve format; should be rare if above loops tried
                    result.Pages.Add(new ProcessedImage
                    {
                        PageNumber = 1,
                        MimeType = "image/jpeg",
                        FileExtension = ".jpg",
                        SuggestedFileName = $"{baseName}-page-001.jpg",
                        Width = frame.Width,
                        Height = frame.Height,
                        Bytes = originalBytes
                    });
                    return result;
                }

                // PNG path: quantize then downscale + quantize; always keep PNG
                if (PngExtensions.Contains(extension))
                {
                    foreach (var colors in new[] { 256, 128, 64 })
                    {
                        var pngCandidate = frame.Clone();
                        var qSettings = new QuantizeSettings
                        {
                            Colors = colors,
                            DitherMethod = DitherMethod.FloydSteinberg
                        };
                        pngCandidate.Quantize(qSettings);
                        pngCandidate.Format = MagickFormat.Png;
                        pngCandidate.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                        pngCandidate.Settings.SetDefine(MagickFormat.Png, "filter", "5");
                        var pngBytes = WriteToBytes(pngCandidate);
                        if (pngBytes.LongLength < (long)originalBytes.LongLength)
                        {
                            result.Pages.Add(new ProcessedImage
                            {
                                PageNumber = 1,
                                MimeType = "image/png",
                                FileExtension = ".png",
                                SuggestedFileName = $"{baseName}-page-001.png",
                                Width = pngCandidate.Width,
                                Height = pngCandidate.Height,
                                Bytes = pngBytes
                            });
                            return result;
                        }
                    }
                    foreach (var pct in new[] { 95, 90, 85, 80, 75 })
                    {
                        using var resized = frame.Clone();
                        resized.FilterType = FilterType.Lanczos;
                        resized.Resize(new Percentage(pct));
                        var pngCandidate = resized.Clone();
                        var qSettings = new QuantizeSettings
                        {
                            Colors = 256,
                            DitherMethod = DitherMethod.FloydSteinberg
                        };
                        pngCandidate.Quantize(qSettings);
                        pngCandidate.Format = MagickFormat.Png;
                        pngCandidate.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                        pngCandidate.Settings.SetDefine(MagickFormat.Png, "filter", "5");
                        var pngBytes = WriteToBytes(pngCandidate);
                        if (pngBytes.LongLength < (long)originalBytes.LongLength)
                        {
                            result.Pages.Add(new ProcessedImage
                            {
                                PageNumber = 1,
                                MimeType = "image/png",
                                FileExtension = ".png",
                                SuggestedFileName = $"{baseName}-page-001.png",
                                Width = pngCandidate.Width,
                                Height = pngCandidate.Height,
                                Bytes = pngBytes
                            });
                            return result;
                        }
                    }
                    result.Pages.Add(new ProcessedImage
                    {
                        PageNumber = 1,
                        MimeType = "image/png",
                        FileExtension = ".png",
                        SuggestedFileName = $"{baseName}-page-001.png",
                        Width = frame.Width,
                        Height = frame.Height,
                        Bytes = originalBytes
                    });
                    return result;
                }

                // WEBP path: keep WEBP if original was WEBP; quality ramp then downscale
                if (WebpExtensions.Contains(extension))
                {
                    foreach (var q in new[] { 90, 85, 80, 75, 70, 65, 60 })
                    {
                        using var webp = frame.Clone();
                        webp.Format = MagickFormat.WebP;
                        webp.Quality = q;
                        webp.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                        webp.Settings.SetDefine(MagickFormat.WebP, "auto-filter", "true");
                        var webpBytes = WriteToBytes(webp);
                        if (webpBytes.LongLength < (long)originalBytes.LongLength)
                        {
                            result.Pages.Add(new ProcessedImage
                            {
                                PageNumber = 1,
                                MimeType = "image/webp",
                                FileExtension = ".webp",
                                SuggestedFileName = $"{baseName}-page-001.webp",
                                Width = webp.Width,
                                Height = webp.Height,
                                Bytes = webpBytes
                            });
                            return result;
                        }
                    }
                    foreach (var pct in new[] { 95, 90, 85, 80, 75 })
                    {
                        using var resized = frame.Clone();
                        resized.FilterType = FilterType.Lanczos;
                        resized.Resize(new Percentage(pct));
                        foreach (var q in new[] { 85, 80, 75, 70, 65 })
                        {
                            using var webp = resized.Clone();
                            webp.Format = MagickFormat.WebP;
                            webp.Quality = q;
                            webp.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                            webp.Settings.SetDefine(MagickFormat.WebP, "auto-filter", "true");
                            var webpBytes = WriteToBytes(webp);
                            if (webpBytes.LongLength < (long)originalBytes.LongLength)
                            {
                                result.Pages.Add(new ProcessedImage
                                {
                                    PageNumber = 1,
                                    MimeType = "image/webp",
                                    FileExtension = ".webp",
                                    SuggestedFileName = $"{baseName}-page-001.webp",
                                    Width = webp.Width,
                                    Height = webp.Height,
                                    Bytes = webpBytes
                                });
                                return result;
                            }
                        }
                    }
                    result.Pages.Add(new ProcessedImage
                    {
                        PageNumber = 1,
                        MimeType = "image/webp",
                        FileExtension = ".webp",
                        SuggestedFileName = $"{baseName}-page-001.webp",
                        Width = frame.Width,
                        Height = frame.Height,
                        Bytes = originalBytes
                    });
                    return result;
                }

                // Other formats: fall back to previous heuristic (EncodeToTargetBlob) to maintain preview compatibility
                var fallbackBlob = EncodeToTargetBlob(frame, targetFormat, 1, baseName, /*sourceIsPdf*/ false);
                result.Pages.Add(fallbackBlob);
                return result;
            }

            foreach (var frame in collection)
            {
                frameIndex++;
                var blob = EncodeToTargetBlob(frame, targetFormat, frameIndex, Path.GetFileNameWithoutExtension(originalName), /*sourceIsPdf*/ false);
                result.Pages.Add(blob);
            }
        }

        return result;
    }

    private static ProcessedImage EncodeToTargetBlob(IMagickImage<byte> frame, MagickFormat targetFormat, int index, string baseName, bool sourceIsPdf)
    {
        using var image = frame.Clone();

        // Normalize for viewing
        // Some PDFs produce transparent pixels or non-sRGB profiles; ensure consistent output.
        image.BackgroundColor = MagickColors.White;
        if (image.HasAlpha)
        {
            // Composite against white and drop alpha to avoid dark/odd backgrounds
            image.Alpha(AlphaOption.Remove);
        }
        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;
        image.Strip(); // remove metadata
        // Subtle sharpening to improve crispness of small text without visible halos
        image.UnsharpMask(0, 0.9, 0.9, 0.02);

        // Optional: additional cleanup could be applied here (deskew/denoise) if needed.

        // Choose encoding parameters based on target format with size-aware optimization between PNG and JPEG
        if (targetFormat == MagickFormat.WebP)
        {
            using var webp = image.Clone();
            webp.Format = MagickFormat.WebP;
            webp.Quality = 90;
            webp.Settings.SetDefine(MagickFormat.WebP, "method", "6");
            webp.Settings.SetDefine(MagickFormat.WebP, "auto-filter", "true");
            var webpBytes = WriteToBytes(webp);
            var suggestedWebp = $"{baseName}-page-{index:D3}.webp";
            return new ProcessedImage
            {
                PageNumber = index,
                MimeType = "image/webp",
                FileExtension = ".webp",
                SuggestedFileName = suggestedWebp,
                Width = webp.Width,
                Height = webp.Height,
                Bytes = webpBytes
            };
        }

        if (targetFormat == MagickFormat.Jpeg)
        {
            var (jpegBytes, jpegWidth, jpegHeight) = EncodeJpeg(image, quality: 90);
            var suggestedJpeg = $"{baseName}-page-{index:D3}.jpg";
            return new ProcessedImage
            {
                PageNumber = index,
                MimeType = "image/jpeg",
                FileExtension = ".jpg",
                SuggestedFileName = suggestedJpeg,
                Width = jpegWidth,
                Height = jpegHeight,
                Bytes = jpegBytes
            };
        }

        // Default branch: target PNG, but attempt JPEG as well and pick the smaller if it's significantly smaller
        // Prepare PNG candidate (with palette quantization to shrink file size for document-like pages)
        var pngCandidate = image.Clone();
        if (!sourceIsPdf)
        {
            var qSettings = new QuantizeSettings
            {
                // For non-PDF images we can safely quantize to reduce size
                Colors = 1024,
                DitherMethod = DitherMethod.FloydSteinberg
            };
            pngCandidate.Quantize(qSettings);
        }
        pngCandidate.Format = MagickFormat.Png;
        pngCandidate.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        pngCandidate.Settings.SetDefine(MagickFormat.Png, "filter", "5");
        var pngBytes = WriteToBytes(pngCandidate);

        // Prepare JPEG candidate at high quality (visually safe) for photo-like pages
        var (jpegCandidateBytes, jpegW, jpegH) = EncodeJpeg(image, quality: 95);

        // Heuristic: prefer PNG unless JPEG is notably smaller to avoid text artifacts
        bool chooseJpeg = jpegCandidateBytes.LongLength < (long)(pngBytes.LongLength * 0.85);

        if (chooseJpeg)
        {
            var suggestedJpeg = $"{baseName}-page-{index:D3}.jpg";
            return new ProcessedImage
            {
                PageNumber = index,
                MimeType = "image/jpeg",
                FileExtension = ".jpg",
                SuggestedFileName = suggestedJpeg,
                Width = jpegW,
                Height = jpegH,
                Bytes = jpegCandidateBytes
            };
        }
        else
        {
            var suggestedPng = $"{baseName}-page-{index:D3}.png";
            return new ProcessedImage
            {
                PageNumber = index,
                MimeType = "image/png",
                FileExtension = ".png",
                SuggestedFileName = suggestedPng,
                Width = pngCandidate.Width,
                Height = pngCandidate.Height,
                Bytes = pngBytes
            };
        }
    }

    private static MagickFormat GetTargetFormat(string extension, bool isPdf)
    {
        if (isPdf)
        {
            // For PDFs (invoice-like), PNG preserves sharp text with lossless compression.
            return MagickFormat.Png;
        }

        if (PngExtensions.Contains(extension))
            return MagickFormat.Png;
        if (JpegExtensions.Contains(extension))
            return MagickFormat.Jpeg;
        if (WebpExtensions.Contains(extension))
            return MagickFormat.WebP; // keep original if the source is already WebP

        // Formats not ideal for web preview are converted to PNG
        if (TiffExtensions.Contains(extension) ||
            GifExtensions.Contains(extension) ||
            HeicExtensions.Contains(extension) ||
            BmpExtensions.Contains(extension))
        {
            return MagickFormat.Png;
        }

        // Default to PNG for unknown formats to ensure browser preview works
        return MagickFormat.Png;
    }

    private static (byte[] bytes, int width, int height) EncodeJpeg(IMagickImage<byte> src, int quality)
    {
        using var jpeg = src.Clone();
        jpeg.Format = MagickFormat.Jpeg;
        jpeg.Quality = quality;
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "trellis-quantization", "true");
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "overshoot-deringing", "true");
        // Favor smaller size: progressive + 4:2:0 subsampling
        jpeg.Interlace = Interlace.Plane;
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "2x2,1x1,1x1");
        var bytes = WriteToBytes(jpeg);
        return (bytes, jpeg.Width, jpeg.Height);
    }

    private static byte[] WriteToBytes(IMagickImage<byte> img)
    {
        using var ms = new MemoryStream();
        img.Write(ms);
        return ms.ToArray();
    }

    private static ProcessedImage EncodeToPngLosslessBlob(IMagickImage<byte> frame, int index, string baseName)
    {
        using var image = frame.Clone();
        image.BackgroundColor = MagickColors.White;
        if (image.HasAlpha)
        {
            image.Alpha(AlphaOption.Remove);
        }
        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;
        image.Strip();
        image.UnsharpMask(0, 0.9, 0.9, 0.02);

        image.Format = MagickFormat.Png;
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        image.Settings.SetDefine(MagickFormat.Png, "filter", "5");

        var bytes = WriteToBytes(image);
        return new ProcessedImage
        {
            PageNumber = index,
            MimeType = "image/png",
            FileExtension = ".png",
            SuggestedFileName = $"{baseName}-page-{index:D3}.png",
            Width = image.Width,
            Height = image.Height,
            Bytes = bytes
        };
    }
}



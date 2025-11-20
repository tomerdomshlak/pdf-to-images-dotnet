using ImageMagick;
using Microsoft.AspNetCore.Http;
using PdfToImages.Api.Models;

namespace PdfToImages.Api.Services;

public sealed class ImageProcessorMagick : IImageProcessor
{
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };
    private static readonly HashSet<string> PngExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png" };
    private static readonly HashSet<string> WebpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".webp" };
    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };
    private static readonly HashSet<string> GifExtensions = new(StringComparer.OrdinalIgnoreCase) { ".gif" };
    private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };
    private static readonly HashSet<string> BmpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".bmp" };

    public async Task<FileConversionResponse> ProcessFileAsync(IFormFile file, ProcessingMode mode, CancellationToken cancellationToken)
    {
        var processed = await ProcessFileToBlobsAsync(file, mode, cancellationToken);
        var fileResponse = new FileConversionResponse { OriginalFileName = processed.OriginalFileName, Pages = new List<ImagePageResponse>() };
        foreach (var page in processed.Pages)
        {
            var base64 = Convert.ToBase64String(page.Bytes);
            var dataUrl = $"data:{page.MimeType};base64,{base64}";
            fileResponse.Pages.Add(new ImagePageResponse
            {
                PageNumber = page.PageNumber,
                MimeType = page.MimeType,
                DataUrl = dataUrl,
                Width = page.Width,
                Height = page.Height,
                SizeBytes = page.Bytes.LongLength
            });
        }

        return fileResponse;
    }

    public async Task<ProcessedFile> ProcessFileToBlobsAsync(IFormFile file, ProcessingMode mode, CancellationToken cancellationToken)
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
                var blob = mode == ProcessingMode.Lossless
                    ? EncodeToPngLosslessBlob(page, pageIndex, Path.GetFileNameWithoutExtension(originalName))
                    : EncodeToTargetBlob(page, targetFormat, pageIndex, Path.GetFileNameWithoutExtension(originalName), /*sourceIsPdf*/ true);
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
            // Lossless path for single-frame common formats
            if (collection.Count == 1 && mode == ProcessingMode.Lossless)
            {
                var frame = collection[0];
                var baseName = Path.GetFileNameWithoutExtension(originalName);

                if (JpegExtensions.Contains(extension))
                {
                    // Return original JPEG bytes to avoid any recompression (lossless guarantee)
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

                if (WebpExtensions.Contains(extension))
                {
                    // Keep original WEBP bytes (could be lossless or lossy originally, but we don't degrade further)
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

                if (PngExtensions.Contains(extension))
                {
                    // Repack PNG losslessly and pick the smaller of original vs repacked
                    var repacked = frame.Clone();
                    repacked.Format = MagickFormat.Png;
                    repacked.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                    repacked.Settings.SetDefine(MagickFormat.Png, "filter", "5");
                    var repackedBytes = WriteToBytes(repacked);
                    var chosen = repackedBytes.LongLength < (long)originalBytes.LongLength ? repackedBytes : originalBytes;

                    result.Pages.Add(new ProcessedImage
                    {
                        PageNumber = 1,
                        MimeType = "image/png",
                        FileExtension = ".png",
                        SuggestedFileName = $"{baseName}-page-001.png",
                        Width = frame.Width,
                        Height = frame.Height,
                        Bytes = chosen
                    });
                    return result;
                }
            }

            foreach (var frame in collection)
            {
                frameIndex++;
                var blob = mode == ProcessingMode.Lossless
                    ? EncodeToPngLosslessBlob(frame, frameIndex, Path.GetFileNameWithoutExtension(originalName))
                    : EncodeToTargetBlob(frame, targetFormat, frameIndex, Path.GetFileNameWithoutExtension(originalName), /*sourceIsPdf*/ false);
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
        jpeg.Quality = quality; // 85-92 usually safe
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "trellis-quantization", "true");
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "overshoot-deringing", "true");
        // Use 4:4:4 subsampling for crisper small text and edges (larger file, better detail)
        jpeg.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4");
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



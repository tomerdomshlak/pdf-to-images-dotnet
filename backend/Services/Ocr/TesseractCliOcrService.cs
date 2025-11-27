using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfToImages.Api.Models;
using ImageMagick;

namespace PdfToImages.Api.Services.Ocr;

public sealed class TesseractCliOcrService : IOcrService
{
    public async Task<OcrResult> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pdf-to-images-ocr");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, $"img-{Guid.NewGuid():N}.png");

        // Ensure a PNG input that Tesseract can read regardless of original format
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var img = new MagickImage(ms);
            img.ColorSpace = ColorSpace.sRGB;
            img.Depth = 8;
            if (img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
            }
            img.Format = MagickFormat.Png;
            await using var outStream = new FileStream(inputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            img.Write(outStream);
        }
        catch
        {
            // Fallback: write raw bytes (might still work if already PNG/JPEG)
            await File.WriteAllBytesAsync(inputPath, imageBytes, cancellationToken);
        }
        try
        {
            // Prefer PATH resolution; fall back to common Homebrew locations
            var tesseractExe = "tesseract";
            if (!IsOnPath(tesseractExe))
            {
                var candidates = new[]
                {
                    "/opt/homebrew/bin/tesseract", // Apple Silicon
                    "/usr/local/bin/tesseract"     // Intel mac / Linux
                };
                tesseractExe = candidates.FirstOrDefault(File.Exists) ?? "tesseract";
            }

            var psi = new ProcessStartInfo
            {
                FileName = tesseractExe,
                Arguments = $"{EscapeArg(inputPath)} stdout -l eng --psm 6 tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start tesseract process.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode != 0)
            {
                var err = stderrTask.Result;
                throw new InvalidOperationException($"tesseract exited with code {proc.ExitCode}: {err}");
            }

            var tsv = stdoutTask.Result;
            return ParseTsv(tsv);
        }
        finally
        {
            try { File.Delete(inputPath); } catch { /* ignore */ }
        }
    }

    private static bool IsOnPath(string exe)
    {
        try
        {
            var which = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(which);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeArg(string s)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"\"{s.Replace("\"", "\\\"")}\"";
        }
        return $"\"{s.Replace("\"", "\\\"")}\"";
    }

    private static OcrResult ParseTsv(string tsv)
    {
        // TSV header example: level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
        var lines = tsv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new OcrResult { FullText = string.Empty, Items = new List<OcrItem>() };

        var items = new List<OcrItem>();
        var fullTextBuilder = new StringBuilder();
        var header = lines[0].Split('\t');
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
            indexes[header[i]] = i;

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split('\t');
            if (cols.Length != header.Length) continue;

            int level = GetInt(cols, indexes, "level");
            string text = GetString(cols, indexes, "text").Trim();
            int left = GetInt(cols, indexes, "left");
            int top = GetInt(cols, indexes, "top");
            int width = GetInt(cols, indexes, "width");
            int height = GetInt(cols, indexes, "height");
            float conf = GetFloat(cols, indexes, "conf");

            var lvl = level switch
            {
                2 => OcrLevel.Block,
                3 => OcrLevel.Paragraph,
                4 => OcrLevel.Line,
                5 => OcrLevel.Word,
                _ => OcrLevel.Word
            };

            if (!string.IsNullOrEmpty(text))
            {
                fullTextBuilder.Append(text);
                if (lvl == OcrLevel.Word) fullTextBuilder.Append(' ');
                if (lvl == OcrLevel.Line) fullTextBuilder.AppendLine();
            }

            // Skip empty text for non-word levels to reduce noise
            if (string.IsNullOrEmpty(text) && lvl != OcrLevel.Block && lvl != OcrLevel.Paragraph)
                continue;

            items.Add(new OcrItem
            {
                Id = i.ToString(CultureInfo.InvariantCulture),
                Level = lvl,
                Text = text,
                Confidence = conf,
                Box = new OcrBoundingBox
                {
                    Left = left,
                    Top = top,
                    Width = width,
                    Height = height
                }
            });
        }

        return new OcrResult
        {
            FullText = fullTextBuilder.ToString().Trim(),
            Items = items
        };
    }

    private static int GetInt(string[] cols, IDictionary<string, int> idx, string name)
        => int.TryParse(GetString(cols, idx, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static float GetFloat(string[] cols, IDictionary<string, int> idx, string name)
        => float.TryParse(GetString(cols, idx, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1f;
    private static string GetString(string[] cols, IDictionary<string, int> idx, string name)
        => idx.TryGetValue(name, out var i) && i >= 0 && i < cols.Length ? cols[i] : string.Empty;
}



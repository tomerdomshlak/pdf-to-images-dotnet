using PdfToImages.Api.Models;
using System.Globalization;
using System.Text;
using Tesseract;

namespace PdfToImages.Api.Services.Ocr;

public sealed class TesseractOcrService : IOcrService
{
    private readonly TesseractEngine _engine;

    public TesseractOcrService()
    {
        // TESSDATA_PREFIX should point to a directory containing tessdata folder or the tessdata path itself
        // Example: /usr/share/tesseract-ocr/4.00/tessdata or ./tessdata
        var tessdata = Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "tessdata";
        // Default to English; extend or make configurable as needed
        _engine = new TesseractEngine(tessdata, "eng", EngineMode.Default);
    }

    public Task<OcrResult> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        // Tesseract .NET wrapper is synchronous; wrap in Task.Run to avoid blocking the caller thread
        return Task.Run(() =>
        {
            using var pix = Pix.LoadFromMemory(imageBytes);
            using var page = _engine.Process(pix);
            var fullText = page.GetText() ?? string.Empty;
            var items = new List<OcrItem>();
            var iter = page.GetIterator();
            iter.Begin();

            // Levels we care about
            var levels = new[]
            {
                PageIteratorLevel.Block,
                PageIteratorLevel.Para,
                PageIteratorLevel.TextLine,
                PageIteratorLevel.Word
            };

            int idCounter = 0;
            do
            {
                foreach (var lvl in levels)
                {
                    if (!iter.TryGetBoundingBox(lvl, out var rect))
                        continue;

                    var text = iter.GetText(lvl) ?? string.Empty;
                    float conf;
                    try
                    {
                        conf = iter.GetConfidence(lvl);
                    }
                    catch
                    {
                        conf = page.GetMeanConfidence();
                    }

                    items.Add(new OcrItem
                    {
                        Id = (++idCounter).ToString(CultureInfo.InvariantCulture),
                        Level = lvl switch
                        {
                            PageIteratorLevel.Block => OcrLevel.Block,
                            PageIteratorLevel.Para => OcrLevel.Paragraph,
                            PageIteratorLevel.TextLine => OcrLevel.Line,
                            PageIteratorLevel.Word => OcrLevel.Word,
                            _ => OcrLevel.Word
                        },
                        Text = text.Trim(),
                        Confidence = conf,
                        Box = new OcrBoundingBox
                        {
                            Left = rect.X1,
                            Top = rect.Y1,
                            Width = rect.Width,
                            Height = rect.Height
                        }
                    });
                }
            } while (iter.Next(PageIteratorLevel.Word));

            return new OcrResult
            {
                FullText = fullText.Trim(),
                Items = items
            };
        }, cancellationToken);
    }
}



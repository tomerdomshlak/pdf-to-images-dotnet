using Microsoft.AspNetCore.Mvc;
using PdfToImages.Api.Models;
using PdfToImages.Api.Services;
using System.IO.Compression;

namespace PdfToImages.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ConvertController : ControllerBase
{
    private readonly IImageProcessor _imageProcessor;

    public ConvertController(IImageProcessor imageProcessor)
    {
        _imageProcessor = imageProcessor;
    }

    [HttpPost]
    [RequestSizeLimit(100_000_000)] // 100 MB
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BatchConversionResponse>> Post([FromForm] List<IFormFile> files, [FromQuery] ProcessingMode mode = ProcessingMode.Lossless, CancellationToken cancellationToken = default)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        var response = new BatchConversionResponse
        {
            Files = new List<FileConversionResponse>()
        };

        foreach (var file in files)
        {
            var result = await _imageProcessor.ProcessFileAsync(file, mode, cancellationToken);
            response.Files.Add(result);
        }

        return Ok(response);
    }

    [HttpPost("zip")]
    [RequestSizeLimit(100_000_000)] // 100 MB
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> PostZip([FromForm] List<IFormFile> files, [FromQuery] ProcessingMode mode = ProcessingMode.Lossless, CancellationToken cancellationToken = default)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        await using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                var processed = await _imageProcessor.ProcessFileToBlobsAsync(file, mode, cancellationToken);
                var baseName = Path.GetFileNameWithoutExtension(processed.OriginalFileName);

                // If only a single page/frame -> put at ZIP root; otherwise use a subfolder
                var useRoot = processed.Pages.Count == 1;
                foreach (var page in processed.Pages)
                {
                    var entryName = useRoot
                        ? page.SuggestedFileName
                        : $"{Sanitize(baseName)}/{page.SuggestedFileName}";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(page.Bytes, 0, page.Bytes.Length, cancellationToken);
                }
            }
        }
        ms.Position = 0;

        var fileName = $"converted-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        return File(ms.ToArray(), "application/zip", fileName);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}



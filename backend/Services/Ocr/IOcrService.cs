using System.Threading;
using System.Threading.Tasks;
using PdfToImages.Api.Models;

namespace PdfToImages.Api.Services.Ocr;

public interface IOcrService
{
    Task<OcrResult> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken);
}



using Microsoft.AspNetCore.Http;
using PdfToImages.Api.Models;

namespace PdfToImages.Api.Services;

public interface IImageProcessor
{
    Task<FileConversionResponse> ProcessFileAsync(IFormFile file, CancellationToken cancellationToken);
    Task<ProcessedFile> ProcessFileToBlobsAsync(IFormFile file, CancellationToken cancellationToken);
}



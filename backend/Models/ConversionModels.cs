namespace PdfToImages.Api.Models;

public sealed class BatchConversionResponse
{
    public List<FileConversionResponse> Files { get; set; } = new();
}

public sealed class FileConversionResponse
{
    public string OriginalFileName { get; set; } = string.Empty;
    public List<ImagePageResponse> Pages { get; set; } = new();
}

public sealed class ImagePageResponse
{
    public int PageNumber { get; set; }
    public string MimeType { get; set; } = "image/webp";
    public string DataUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
}



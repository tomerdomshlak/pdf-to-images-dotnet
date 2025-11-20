namespace PdfToImages.Api.Models;

public sealed class ProcessedFile
{
    public string OriginalFileName { get; set; } = string.Empty;
    public List<ProcessedImage> Pages { get; set; } = new();
}

public sealed class ProcessedImage
{
    public int PageNumber { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string SuggestedFileName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}



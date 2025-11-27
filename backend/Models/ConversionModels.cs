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
    public OcrResult? Ocr { get; set; }
}

public enum OcrLevel
{
    Block,
    Paragraph,
    Line,
    Word
}

public sealed class OcrBoundingBox
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class OcrItem
{
    public string Id { get; set; } = string.Empty;
    public OcrLevel Level { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public OcrBoundingBox Box { get; set; } = new();
}

public sealed class OcrResult
{
    public string FullText { get; set; } = string.Empty;
    public List<OcrItem> Items { get; set; } = new();
}



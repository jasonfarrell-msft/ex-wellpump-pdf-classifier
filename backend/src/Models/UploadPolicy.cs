namespace WellpumpClassifier.Models;

public static class UploadPolicy
{
    public const long DocumentIntelligenceMaxPageBytes = 500_000_000;

    public static string? ValidateMetadata(string fileName, long length)
    {
        if (length <= 0) return "Choose a non-empty PDF.";
        if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return "Choose a PDF file.";
        return null;
    }
}

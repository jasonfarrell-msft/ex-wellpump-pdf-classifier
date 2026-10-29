using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WellpumpClassifier.Models;

namespace WellpumpClassifier;

// Files live only in a random temporary directory and are removed after the upload.
public sealed class PdfSplitter : IDisposable
{
    public string DirectoryPath { get; }
    public string OriginalPath => Path.Combine(DirectoryPath, "original.pdf");
    public List<string> Pages { get; } = [];
    public PdfSplitter(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Path.GetTempPath(), $"wellpump-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
    }

    public async Task ReadAsync(Stream input, CancellationToken ct)
    {
        await using (var output = File.Create(OriginalPath))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (total < 5) throw new InvalidDataException("Choose a non-empty PDF.");
        }
        SplitFile(OriginalPath, UploadPolicy.DocumentIntelligenceMaxPageBytes, ct);
    }

    // Called only inside the killable worker subprocess (and deterministic offline tests).
    public void SplitFile(string input, long maxPageBytes, CancellationToken ct)
    {
        if (new FileInfo(input).Length <= 0)
            throw new InvalidDataException("PDF must be non-empty.");
        using (var signature = File.OpenRead(input))
        {
            var header = new byte[5];
            _ = signature.Read(header);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("Invalid PDF signature.");
        }
        try
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            // PDFsharp clears active encryption after opening (including empty-user-password
            // files). The imported encryption dictionary remains in its object table.
            if (source.SecuritySettings.IsEncrypted
                || source.Internals.GetAllObjects().Any(o => o is PdfSharp.Pdf.Security.PdfStandardSecurityHandler))
                throw new InvalidDataException("Encrypted PDFs are not supported.");
            if (source.PageCount < 1)
                throw new InvalidDataException("PDF must contain at least one page.");
            for (var index = 0; index < source.PageCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = new PdfDocument();
                page.AddPage(source.Pages[index]);
                var path = Path.Combine(DirectoryPath, $"{index + 1}.pdf");
                using (var output = new BoundedPdfOutput(path, maxPageBytes))
                    page.Save(output, closeStream: false);
                Pages.Add(path);
            }
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or ArgumentException
            or FormatException or IndexOutOfRangeException or EndOfStreamException)
        {
            throw new InvalidDataException("PDF is malformed, encrypted, or unsupported.");
        }
    }

    public static string SafeName(string name)
    {
        var safe = new string(Path.GetFileName(name.Replace('\\', '/'))
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').Take(120).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "document.pdf" : safe;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

// Bound writes BEFORE they reach disk, not just after a potentially huge PDF save.
// The parent independently checks these limits and terminates the child on timeout.
internal sealed class BoundedPdfOutput(string path, long maximum) : Stream
{
    private readonly FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite);
    public override bool CanRead => file.CanRead;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set { Check(value); file.Position = value; } }
    private void Check(long size)
    {
        if (size < 0 || size > maximum) throw new InvalidDataException("A page exceeds the Document Intelligence service size limit.");
    }
    public override void Flush() => file.Flush();
    public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset };
        Check(target);
        return file.Seek(offset, origin);
    }
    public override void SetLength(long value) { Check(value); file.SetLength(value); }
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(Math.Max(Length, checked(Position + count)));
        file.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(Math.Max(Length, checked(Position + buffer.Length)));
        file.Write(buffer);
    }
    protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
}

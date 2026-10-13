using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ETS2LA.Game.Perception;

public static class TollPrompt
{
    public static bool IsPaymentRequest(string text)
    {
        string normalized = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return Regex.IsMatch(normalized, @"\b(toll|peage)\b") &&
               Regex.IsMatch(normalized, @"\b(pay|payer|paiement|payment)\b") &&
               Regex.IsMatch(normalized, @"\b(enter|entree|return)\b");
    }
}
#if WINDOWS
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TollPromptReader : IDisposable
{
    private readonly Tesseract.TesseractEngine engine;
    public TollPromptReader(string folder) => engine = new Tesseract.TesseractEngine(Path.Combine(folder, "tessdata"), "eng+fra", Tesseract.EngineMode.LstmOnly);
    public bool Read(System.Drawing.Bitmap frame)
    {
        using var stream = new MemoryStream();
        frame.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Tesseract.Pix.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(pix, Tesseract.PageSegMode.SparseText);
        return TollPrompt.IsPaymentRequest(page.GetText());
    }
    public void Dispose() => engine.Dispose();
}
#endif

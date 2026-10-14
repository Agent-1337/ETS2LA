#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ETS2LA.Logging;

namespace ETS2LA.Game.Perception;

/// <summary>Local, open-vocabulary ONNX detector. No frames or predictions leave the computer.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GroundingDinoDetector : IDisposable
{
    private readonly InferenceSession session;
    private readonly long[] tokens;
    private readonly List<(string Label, int Start, int End)> spans = [];
    private const int InputSize = 800;
    private readonly float[] pixelBuffer = new float[3 * InputSize * InputSize];
    private readonly Bitmap resized = new(InputSize, InputSize, PixelFormat.Format24bppRgb);
    private readonly byte[] pixelRow = new byte[InputSize * 3];
    private readonly List<NamedOnnxValue> inputs = [];
    public string ExecutionProvider { get; }
    public string DeviceName { get; } = "CPU";
    public IReadOnlyDictionary<string, NodeMetadata> Inputs => session.InputMetadata;
    public IReadOnlyDictionary<string, NodeMetadata> Outputs => session.OutputMetadata;
    public string LastDiagnostics { get; private set; } = "";
    private static readonly string[] labels = ["truck", "person", "concrete barrier", "fence", "traffic cone", "road sign", "mirror", "car"];

    public GroundingDinoDetector(string folder, string[]? candidateLabels = null)
    {
        using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            int selected = 0; long mostMemory = -1;
            var adapters = factory.Adapters1;
            try
            {
                for (int i = 0; i < adapters.Length; i++)
                {
                    var description = adapters[i].Description1;
                    if ((description.Flags & SharpDX.DXGI.AdapterFlags.Software) != 0) continue;
                    long dedicated = (long)description.DedicatedVideoMemory;
                    if (dedicated > mostMemory) { selected = i; mostMemory = dedicated; DeviceName = description.Description.Trim(); }
                }
            }
            finally { foreach (var adapter in adapters) adapter.Dispose(); }
            options.AppendExecutionProvider_DML(selected);
            ExecutionProvider = "DirectML";
        }
        catch (Exception ex) { Logger.Warn($"DirectML unavailable: {ex.Message}. Visual detection will use CPU."); ExecutionProvider = "CPU"; DeviceName = "CPU"; }
        try { session = new InferenceSession(Path.Combine(folder, "grounding-dino.onnx"), options); }
        catch when (ExecutionProvider == "DirectML")
        {
            using var cpu = new SessionOptions { IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };
            session = new InferenceSession(Path.Combine(folder, "grounding-dino.onnx"), cpu);
            ExecutionProvider = "CPU";
            DeviceName = "CPU";
        }
        var vocabulary = File.ReadAllLines(Path.Combine(folder, "vocab.txt"))
            .Select((word, id) => (word, id)).ToDictionary(x => x.word, x => x.id);
        var ids = new List<long> { vocabulary["[CLS]"] };
        foreach (var label in candidateLabels ?? labels)
        {
            int start = ids.Count;
            foreach (Match token in Regex.Matches(label, @"[a-z]+|[^\w\s]"))
            {
                string word = token.Value;
                if (vocabulary.TryGetValue(word, out int id)) { ids.Add(id); continue; }
                for (int offset = 0; offset < word.Length;)
                {
                    int end = word.Length;
                    while (end > offset && !vocabulary.ContainsKey((offset == 0 ? "" : "##") + word[offset..end])) end--;
                    if (end == offset) { ids.Add(vocabulary["[UNK]"]); break; }
                    ids.Add(vocabulary[(offset == 0 ? "" : "##") + word[offset..end]]);
                    offset = end;
                }
            }
            spans.Add((label, start, ids.Count));
            ids.Add(vocabulary["."]);
        }
        ids.Add(vocabulary["[SEP]"]);
        tokens = ids.ToArray();
        inputs.Add(NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(pixelBuffer, [1, 3, InputSize, InputSize])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokens, [1, tokens.Length])));
        foreach (var name in session.InputMetadata.Keys)
        {
            if (name == "attention_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(Enumerable.Repeat(1L, tokens.Length).ToArray(), [1, tokens.Length])));
            if (name == "token_type_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>([1, tokens.Length])));
            if (name == "pixel_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(Enumerable.Repeat(1L, InputSize * InputSize).ToArray(), [1, InputSize, InputSize])));
        }
    }

    public IReadOnlyList<ObjectDetection> Detect(Bitmap image, float minimumConfidence = 0.28f)
    {
        const int size = InputSize;
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(image, new Rectangle(0, 0, size, size));
        }
        var data = resized.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixelRow, 0, pixelRow.Length);
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x, rgb = x * 3;
                    pixelBuffer[index] = (pixelRow[rgb + 2] / 255f - 0.485f) / 0.229f;
                    pixelBuffer[size * size + index] = (pixelRow[rgb + 1] / 255f - 0.456f) / 0.224f;
                    pixelBuffer[2 * size * size + index] = (pixelRow[rgb] / 255f - 0.406f) / 0.225f;
                }
            }
        }
        finally { resized.UnlockBits(data); }
        using var outputs = session.Run(inputs);
        var logits = outputs.First(x => x.Name == "logits").AsTensor<float>();
        var boxes = outputs.First(x => x.Name == "pred_boxes").AsTensor<float>();
        ReadOnlySpan<float> scores = logits is DenseTensor<float> denseScores ? denseScores.Buffer.Span : logits.ToArray();
        ReadOnlySpan<float> coordinates = boxes is DenseTensor<float> denseBoxes ? denseBoxes.Buffer.Span : boxes.ToArray();
        int finite = 0; float maximum = float.NegativeInfinity;
        foreach (float value in scores) if (float.IsFinite(value)) { finite++; maximum = Math.Max(maximum, value); }
        if (finite == 0) throw new InvalidDataException("The visual detector produced no finite token scores.");
        LastDiagnostics = $"Finite logits {finite}/{logits.Length}; max {maximum}";
        var results = new List<ObjectDetection>();
        for (int q = 0; q < logits.Dimensions[1]; q++)
        {
            string label = "";
            float bestLogit = float.NegativeInfinity;
            foreach (var span in spans)
            {
                float candidate = float.NegativeInfinity;
                for (int i = span.Start; i < span.End; i++) candidate = Math.Max(candidate, scores[q * logits.Dimensions[2] + i]);
                if (candidate > bestLogit) { bestLogit = candidate; label = span.Label; }
            }
            float score = 1 / (1 + MathF.Exp(-bestLogit));
            if (score < minimumConfidence) continue;
            float cx = coordinates[q * 4] * image.Width, cy = coordinates[q * 4 + 1] * image.Height;
            float w = coordinates[q * 4 + 2] * image.Width, h = coordinates[q * 4 + 3] * image.Height;
            if (!float.IsFinite(cx + cy + w + h) || w < 3 || h < 3) continue;
            results.Add(new(label, score, new(Math.Clamp(cx - w / 2, 0, image.Width), Math.Clamp(cy - h / 2, 0, image.Height), Math.Clamp(cx + w / 2, 0, image.Width), Math.Clamp(cy + h / 2, 0, image.Height))));
        }
        var kept = new List<ObjectDetection>();
        foreach (var detection in results.OrderByDescending(x => x.Confidence))
            if (kept.All(other => IntersectionOverUnion(other.Box, detection.Box) < 0.45f)) kept.Add(detection);
        return kept;
    }

    private static float IntersectionOverUnion(ImageBox a, ImageBox b)
    {
        float intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / Math.Max(1, (a.Right - a.Left) * (a.Bottom - a.Top) + (b.Right - b.Left) * (b.Bottom - b.Top) - intersection);
    }

    public void Dispose() { resized.Dispose(); session.Dispose(); }
}
#endif

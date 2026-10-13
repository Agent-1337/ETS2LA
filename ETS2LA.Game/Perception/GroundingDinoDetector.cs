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
    public string ExecutionProvider { get; }
    public string DeviceName { get; } = "CPU";
    public IReadOnlyDictionary<string, NodeMetadata> Inputs => session.InputMetadata;
    public IReadOnlyDictionary<string, NodeMetadata> Outputs => session.OutputMetadata;
    public string LastDiagnostics { get; private set; } = "";
    private static readonly string[] labels = ["truck", "person", "concrete barrier", "fence", "traffic cone"];

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
    }

    public IReadOnlyList<ObjectDetection> Detect(Bitmap image, float minimumConfidence = 0.28f)
    {
        const int size = 800;
        using var resized = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(image, new Rectangle(0, 0, size, size));
        }
        var pixels = new DenseTensor<float>(new[] { 1, 3, size, size });
        var data = resized.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[data.Stride];
            float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < size; x++)
                    for (int c = 0; c < 3; c++) pixels[0, c, y, x] = (row[x * 3 + 2 - c] / 255f - mean[c]) / std[c];
            }
        }
        finally { resized.UnlockBits(data); }
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("pixel_values", pixels),
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokens, new[] { 1, tokens.Length })) };
        foreach (var name in session.InputMetadata.Keys)
        {
            if (name == "attention_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(Enumerable.Repeat(1L, tokens.Length).ToArray(), new[] { 1, tokens.Length })));
            if (name == "token_type_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { 1, tokens.Length })));
            if (name == "pixel_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(Enumerable.Repeat(1L, size * size).ToArray(), new[] { 1, size, size })));
        }
        using var outputs = session.Run(inputs);
        var logits = outputs.First(x => x.Name == "logits").AsTensor<float>();
        var boxes = outputs.First(x => x.Name == "pred_boxes").AsTensor<float>();
        var finiteLogits = logits.ToArray().Where(float.IsFinite).ToArray();
        if (finiteLogits.Length == 0) throw new InvalidDataException("The visual detector produced no finite token scores.");
        LastDiagnostics = $"Finite logits {finiteLogits.Length}/{logits.Length}; max {(finiteLogits.Length > 0 ? finiteLogits.Max() : float.NaN)}; tokens {string.Join(',', tokens)}";
        var results = new List<ObjectDetection>();
        for (int q = 0; q < logits.Dimensions[1]; q++)
        {
            string label = "";
            float score = 0;
            foreach (var span in spans)
            {
                float candidate = 0;
                for (int i = span.Start; i < span.End; i++) candidate = Math.Max(candidate, 1 / (1 + MathF.Exp(-logits[0, q, i])));
                if (candidate > score) { score = candidate; label = span.Label; }
            }
            if (score < minimumConfidence) continue;
            float cx = boxes[0, q, 0] * image.Width, cy = boxes[0, q, 1] * image.Height;
            float w = boxes[0, q, 2] * image.Width, h = boxes[0, q, 3] * image.Height;
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

    public void Dispose() => session.Dispose();
}
#endif

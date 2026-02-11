using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChargingPanel.Core.OCR.Recognition;

/// <summary>
/// 内置数字识别引擎：用于识别 HUD 数字血量（无需外部模型）。
/// </summary>
internal sealed class OcrDigitRecognitionEngine
{
    private const int TemplateWidth = 5;
    private const int TemplateHeight = 7;
    private const int MinDigitSimilarity = 58; // 0-100
    private const int MinSegmentSimilarity = 57; // 0-100
    private static readonly Dictionary<int, bool[]> DigitTemplates = BuildDigitTemplates();
    private static readonly Dictionary<int, bool[]> SegmentPatterns = BuildSegmentPatterns();

    public bool TryAnalyzeHealthPercent(Image<Rgba32> image, out OCRResult result)
    {
        result = new OCRResult { Success = false, Error = "No digital value detected" };
        if (image.Width < 8 || image.Height < 8)
        {
            return false;
        }

        if (!BuildBinaryMask(image, out var mask))
        {
            return false;
        }

        RemoveIsolatedPixels(mask);

        var minArea = Math.Max(8, (image.Width * image.Height) / 700);
        var minHeight = Math.Max(4, image.Height / 3);
        var components = ExtractConnectedComponents(mask, minArea, minHeight);
        if (components.Count == 0)
        {
            return false;
        }

        components.Sort((a, b) => a.MinX.CompareTo(b.MinX));

        var chars = new List<char>(components.Count);
        var confidenceSum = 0.0;
        foreach (var component in components)
        {
            if (TryRecognizeDigit(mask, component, out var digit, out var similarity))
            {
                chars.Add((char)('0' + digit));
                confidenceSum += similarity;
            }
        }

        if (chars.Count == 0)
        {
            return false;
        }

        var text = new string(chars.ToArray());
        if (!int.TryParse(text, out var numericValue))
        {
            return false;
        }

        var averageConfidence = confidenceSum / chars.Count;
        var normalizedPercent = numericValue <= 100 ? numericValue : 100;

        result = new OCRResult
        {
            Success = true,
            HealthPercent = Math.Clamp(normalizedPercent, 0, 100),
            NumericValue = numericValue,
            Confidence = Math.Clamp(averageConfidence, 0, 1),
            TotalPixels = image.Width * image.Height
        };
        return true;
    }

    private static bool BuildBinaryMask(Image<Rgba32> image, out bool[,] mask)
    {
        mask = new bool[image.Width, image.Height];
        var histogram = new int[256];
        var luminance = new byte[image.Width * image.Height];

        var idx = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var luma = (byte)Math.Clamp((int)Math.Round(p.R * 0.299 + p.G * 0.587 + p.B * 0.114), 0, 255);
                    luminance[idx++] = luma;
                    histogram[luma]++;
                }
            }
        });

        var threshold = ComputeOtsuThreshold(histogram, image.Width * image.Height);
        var brightCount = 0;
        var darkCount = 0;
        for (var i = 0; i < luminance.Length; i++)
        {
            if (luminance[i] >= threshold)
            {
                brightCount++;
            }
            else
            {
                darkCount++;
            }
        }

        var brightAsForeground = brightCount < darkCount;
        var foregroundPixels = FillMask(image.Width, image.Height, luminance, threshold, brightAsForeground, mask);
        var ratio = (double)foregroundPixels / Math.Max(1, luminance.Length);

        if (ratio < 0.003 || ratio > 0.82)
        {
            foregroundPixels = FillMask(image.Width, image.Height, luminance, threshold, !brightAsForeground, mask);
            ratio = (double)foregroundPixels / Math.Max(1, luminance.Length);
        }

        return ratio >= 0.003 && ratio <= 0.82;
    }

    private static int FillMask(
        int width,
        int height,
        byte[] luminance,
        int threshold,
        bool brightAsForeground,
        bool[,] mask)
    {
        var count = 0;
        var idx = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var fg = brightAsForeground
                    ? luminance[idx] > threshold
                    : luminance[idx] < threshold;
                mask[x, y] = fg;
                if (fg)
                {
                    count++;
                }
                idx++;
            }
        }

        return count;
    }

    private static int ComputeOtsuThreshold(int[] histogram, int totalPixels)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += i * histogram[i];
        }

        long sumBackground = 0;
        var weightBackground = 0;
        var maxVariance = 0.0;
        var threshold = 127;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = totalPixels - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += t * histogram[t];
            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sum - sumBackground) / weightForeground;
            var variance = weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = t;
            }
        }

        return threshold;
    }

    private static void RemoveIsolatedPixels(bool[,] mask)
    {
        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var toClear = new List<(int x, int y)>();

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                if (!mask[x, y])
                {
                    continue;
                }

                var neighbors = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        if (mask[x + dx, y + dy])
                        {
                            neighbors++;
                        }
                    }
                }

                if (neighbors <= 1)
                {
                    toClear.Add((x, y));
                }
            }
        }

        foreach (var (x, y) in toClear)
        {
            mask[x, y] = false;
        }
    }

    private static List<Component> ExtractConnectedComponents(bool[,] mask, int minArea, int minHeight)
    {
        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var visited = new bool[width, height];
        var components = new List<Component>();
        var queue = new Queue<(int x, int y)>();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[x, y] || visited[x, y])
                {
                    continue;
                }

                var minX = x;
                var minY = y;
                var maxX = x;
                var maxY = y;
                var area = 0;

                queue.Enqueue((x, y));
                visited[x, y] = true;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    area++;
                    minX = Math.Min(minX, cx);
                    minY = Math.Min(minY, cy);
                    maxX = Math.Max(maxX, cx);
                    maxY = Math.Max(maxY, cy);

                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nx = cx + dx;
                            var ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            {
                                continue;
                            }

                            if (!visited[nx, ny] && mask[nx, ny])
                            {
                                visited[nx, ny] = true;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                }

                var component = new Component(minX, minY, maxX, maxY, area);
                if (component.Area >= minArea && component.Height >= minHeight)
                {
                    components.Add(component);
                }
            }
        }

        return components;
    }

    private static bool TryRecognizeDigit(bool[,] mask, Component component, out int digit, out double similarity)
    {
        digit = 0;
        similarity = 0;

        var bestDigit = -1;
        var bestScore = -1.0;

        foreach (var kv in DigitTemplates)
        {
            var score = EvaluateTemplateSimilarity(mask, component, kv.Value);
            if (score > bestScore)
            {
                bestScore = score;
                bestDigit = kv.Key;
            }
        }

        if (bestDigit >= 0 && bestScore * 100 >= MinDigitSimilarity)
        {
            digit = bestDigit;
            similarity = bestScore;
            return true;
        }

        if (TryRecognizeBySegments(mask, component, out digit, out similarity))
        {
            return true;
        }

        return false;
    }

    private static double EvaluateTemplateSimilarity(bool[,] mask, Component component, bool[] template)
    {
        var w = component.Width;
        var h = component.Height;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var matches = 0;
        var total = w * h;
        for (var y = 0; y < h; y++)
        {
            var ty = Math.Clamp((y * TemplateHeight) / h, 0, TemplateHeight - 1);
            for (var x = 0; x < w; x++)
            {
                var tx = Math.Clamp((x * TemplateWidth) / w, 0, TemplateWidth - 1);
                var expected = template[ty * TemplateWidth + tx];
                var actual = mask[component.MinX + x, component.MinY + y];
                if (expected == actual)
                {
                    matches++;
                }
            }
        }

        return (double)matches / total;
    }

    private static bool TryRecognizeBySegments(bool[,] mask, Component component, out int digit, out double similarity)
    {
        digit = 0;
        similarity = 0;
        if (component.Width < 3 || component.Height < 5)
        {
            return false;
        }

        var segments = ExtractSevenSegments(mask, component);
        var bestDigit = -1;
        var bestScore = -1.0;

        foreach (var kv in SegmentPatterns)
        {
            var pattern = kv.Value;
            var matches = 0;
            for (var i = 0; i < 7; i++)
            {
                if (segments[i] == pattern[i])
                {
                    matches++;
                }
            }

            var score = matches / 7.0;
            if (score > bestScore)
            {
                bestScore = score;
                bestDigit = kv.Key;
            }
        }

        if (bestDigit < 0 || bestScore * 100 < MinSegmentSimilarity)
        {
            return false;
        }

        digit = bestDigit;
        similarity = bestScore;
        return true;
    }

    private static bool[] ExtractSevenSegments(bool[,] mask, Component component)
    {
        var w = component.Width;
        var h = component.Height;
        var quarterW = Math.Max(1, w / 4);
        var topBand = Math.Max(1, h / 5);
        var midBand = Math.Max(1, h / 7);
        var halfY = component.MinY + h / 2;
        var left = component.MinX;
        var right = component.MaxX;
        var top = component.MinY;
        var bottom = component.MaxY;

        var centerLeft = left + quarterW;
        var centerRight = right - quarterW;
        if (centerLeft > centerRight)
        {
            centerLeft = left;
            centerRight = right;
        }

        var a = GetRegionFill(mask, centerLeft, centerRight, top, Math.Min(bottom, top + topBand), 0.18);
        var d = GetRegionFill(mask, centerLeft, centerRight, Math.Max(top, bottom - topBand), bottom, 0.18);
        var g = GetRegionFill(mask, centerLeft, centerRight, Math.Max(top, halfY - midBand), Math.Min(bottom, halfY + midBand), 0.18);
        var f = GetRegionFill(mask, left, Math.Min(right, left + quarterW), Math.Min(bottom, top + topBand), Math.Max(top, halfY - 1), 0.16);
        var e = GetRegionFill(mask, left, Math.Min(right, left + quarterW), Math.Min(bottom, halfY), Math.Max(top, bottom - topBand), 0.16);
        var b = GetRegionFill(mask, Math.Max(left, right - quarterW), right, Math.Min(bottom, top + topBand), Math.Max(top, halfY - 1), 0.16);
        var c = GetRegionFill(mask, Math.Max(left, right - quarterW), right, Math.Min(bottom, halfY), Math.Max(top, bottom - topBand), 0.16);

        return new[] { a, b, c, d, e, f, g };
    }

    private static bool GetRegionFill(bool[,] mask, int x0, int x1, int y0, int y1, double threshold)
    {
        if (x1 < x0 || y1 < y0)
        {
            return false;
        }

        var total = 0;
        var hits = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                total++;
                if (mask[x, y])
                {
                    hits++;
                }
            }
        }

        if (total == 0)
        {
            return false;
        }

        return (double)hits / total >= threshold;
    }

    private static Dictionary<int, bool[]> BuildDigitTemplates()
    {
        return new Dictionary<int, bool[]>
        {
            [0] = ParseTemplate(
                "11111",
                "10001",
                "10001",
                "10001",
                "10001",
                "10001",
                "11111"),
            [1] = ParseTemplate(
                "00100",
                "01100",
                "00100",
                "00100",
                "00100",
                "00100",
                "01110"),
            [2] = ParseTemplate(
                "11111",
                "00001",
                "00001",
                "11111",
                "10000",
                "10000",
                "11111"),
            [3] = ParseTemplate(
                "11111",
                "00001",
                "00001",
                "01111",
                "00001",
                "00001",
                "11111"),
            [4] = ParseTemplate(
                "10001",
                "10001",
                "10001",
                "11111",
                "00001",
                "00001",
                "00001"),
            [5] = ParseTemplate(
                "11111",
                "10000",
                "10000",
                "11111",
                "00001",
                "00001",
                "11111"),
            [6] = ParseTemplate(
                "11111",
                "10000",
                "10000",
                "11111",
                "10001",
                "10001",
                "11111"),
            [7] = ParseTemplate(
                "11111",
                "00001",
                "00010",
                "00100",
                "01000",
                "01000",
                "01000"),
            [8] = ParseTemplate(
                "11111",
                "10001",
                "10001",
                "11111",
                "10001",
                "10001",
                "11111"),
            [9] = ParseTemplate(
                "11111",
                "10001",
                "10001",
                "11111",
                "00001",
                "00001",
                "11111")
        };
    }

    private static Dictionary<int, bool[]> BuildSegmentPatterns()
    {
        // [a,b,c,d,e,f,g]
        return new Dictionary<int, bool[]>
        {
            [0] = new[] { true, true, true, true, true, true, false },
            [1] = new[] { false, true, true, false, false, false, false },
            [2] = new[] { true, true, false, true, true, false, true },
            [3] = new[] { true, true, true, true, false, false, true },
            [4] = new[] { false, true, true, false, false, true, true },
            [5] = new[] { true, false, true, true, false, true, true },
            [6] = new[] { true, false, true, true, true, true, true },
            [7] = new[] { true, true, true, false, false, false, false },
            [8] = new[] { true, true, true, true, true, true, true },
            [9] = new[] { true, true, true, true, false, true, true }
        };
    }

    private static bool[] ParseTemplate(params string[] rows)
    {
        var result = new bool[TemplateWidth * TemplateHeight];
        for (var y = 0; y < TemplateHeight; y++)
        {
            var row = y < rows.Length ? rows[y] : "00000";
            for (var x = 0; x < TemplateWidth; x++)
            {
                var c = x < row.Length ? row[x] : '0';
                result[y * TemplateWidth + x] = c == '1';
            }
        }

        return result;
    }

    private readonly record struct Component(int MinX, int MinY, int MaxX, int MaxY, int Area)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
    }
}

using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChargingPanel.Core.OCR.Recognition;

/// <summary>
/// 颜色统计识别引擎（内置模式）。
/// </summary>
internal sealed class OcrColorRecognitionEngine
{
    public OCRResult AnalyzeHealthBar(Image<Rgba32> image, OCRConfig config)
    {
        var healthColor = ParseColorRange(config.HealthColorMin, config.HealthColorMax);
        var bgColor = ParseColorRange(config.BackgroundColorMin, config.BackgroundColorMax);

        var (healthPixels, bgPixels) = CountColorPixels(image, healthColor.min, healthColor.max, bgColor.min, bgColor.max);
        var totalRelevantPixels = healthPixels + bgPixels;
        if (totalRelevantPixels < 10)
        {
            return new OCRResult { Success = false, Error = "No health bar detected" };
        }

        var fillRatio = EstimateFillRatioByLength(image, healthColor.min, healthColor.max, bgColor.min, bgColor.max);
        if (double.IsNaN(fillRatio))
        {
            fillRatio = (double)healthPixels / totalRelevantPixels;
        }

        var healthPercent = (int)Math.Round(100.0 * fillRatio, MidpointRounding.AwayFromZero);
        return new OCRResult
        {
            Success = true,
            HealthPercent = Math.Clamp(healthPercent, 0, 100),
            HealthPixels = healthPixels,
            BackgroundPixels = bgPixels,
            TotalPixels = image.Width * image.Height,
            Confidence = Math.Clamp((double)totalRelevantPixels / Math.Max(1, image.Width * image.Height), 0, 1)
        };
    }

    public OCRResult AnalyzeArmorBar(Image<Rgba32> image, OCRConfig config)
    {
        var armorColor = ParseColorRange(config.ArmorColorMin, config.ArmorColorMax);
        var bgColor = ParseColorRange(config.BackgroundColorMin, config.BackgroundColorMax);

        var (armorPixels, bgPixels) = CountColorPixels(image, armorColor.min, armorColor.max, bgColor.min, bgColor.max);
        var totalRelevantPixels = armorPixels + bgPixels;
        if (totalRelevantPixels < 10)
        {
            return new OCRResult { Success = false, Error = "No armor bar detected" };
        }

        var fillRatio = EstimateFillRatioByLength(image, armorColor.min, armorColor.max, bgColor.min, bgColor.max);
        if (double.IsNaN(fillRatio))
        {
            fillRatio = (double)armorPixels / totalRelevantPixels;
        }

        var armorPercent = (int)Math.Round(100.0 * fillRatio, MidpointRounding.AwayFromZero);
        return new OCRResult
        {
            Success = true,
            Armor = Math.Clamp(armorPercent, 0, 100),
            TotalPixels = image.Width * image.Height,
            Confidence = Math.Clamp((double)totalRelevantPixels / Math.Max(1, image.Width * image.Height), 0, 1)
        };
    }

    private static double EstimateFillRatioByLength(
        Image<Rgba32> image,
        Rgba32 targetMin,
        Rgba32 targetMax,
        Rgba32 bgMin,
        Rgba32 bgMax)
    {
        var width = image.Width;
        var height = image.Height;
        if (width <= 1 || height <= 1)
        {
            return double.NaN;
        }

        var targetByColumn = new int[width];
        var relevantByColumn = new int[width];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    if (IsInColorRange(pixel, targetMin, targetMax))
                    {
                        targetByColumn[x]++;
                        relevantByColumn[x]++;
                    }
                    else if (IsInColorRange(pixel, bgMin, bgMax))
                    {
                        relevantByColumn[x]++;
                    }
                }
            }
        });

        var minRelevantRows = Math.Max(1, height / 5);
        var first = -1;
        var last = -1;
        for (var x = 0; x < width; x++)
        {
            if (relevantByColumn[x] >= minRelevantRows)
            {
                if (first < 0)
                {
                    first = x;
                }

                last = x;
            }
        }

        if (first < 0 || last <= first)
        {
            return double.NaN;
        }

        var ratioByColumn = new double[width];
        for (var x = first; x <= last; x++)
        {
            ratioByColumn[x] = relevantByColumn[x] == 0
                ? 0
                : (double)targetByColumn[x] / relevantByColumn[x];
        }

        var filledBoundary = first - 1;
        for (var x = first; x <= last; x++)
        {
            var smoothed = ratioByColumn[x];
            var samples = 1;
            if (x > first)
            {
                smoothed += ratioByColumn[x - 1];
                samples++;
            }

            if (x < last)
            {
                smoothed += ratioByColumn[x + 1];
                samples++;
            }

            smoothed /= samples;
            if (smoothed >= 0.45)
            {
                filledBoundary = x;
            }
        }

        if (filledBoundary < first)
        {
            return 0;
        }

        var denominator = Math.Max(1, last - first + 1);
        return Math.Clamp((double)(filledBoundary - first + 1) / denominator, 0, 1);
    }

    private static (int target, int background) CountColorPixels(
        Image<Rgba32> image,
        Rgba32 targetMin,
        Rgba32 targetMax,
        Rgba32 bgMin,
        Rgba32 bgMax)
    {
        var targetPixels = 0;
        var backgroundPixels = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    if (IsInColorRange(pixel, targetMin, targetMax))
                    {
                        targetPixels++;
                    }
                    else if (IsInColorRange(pixel, bgMin, bgMax))
                    {
                        backgroundPixels++;
                    }
                }
            }
        });

        return (targetPixels, backgroundPixels);
    }

    private static (Rgba32 min, Rgba32 max) ParseColorRange(string minHex, string maxHex)
    {
        return (ParseHexColor(minHex), ParseHexColor(maxHex));
    }

    private static Rgba32 ParseHexColor(string hex)
    {
        var compact = hex.TrimStart('#');
        if (compact.Length == 6)
        {
            try
            {
                return new Rgba32(
                    Convert.ToByte(compact.Substring(0, 2), 16),
                    Convert.ToByte(compact.Substring(2, 2), 16),
                    Convert.ToByte(compact.Substring(4, 2), 16),
                    255);
            }
            catch
            {
                return new Rgba32(0, 0, 0, 255);
            }
        }

        return new Rgba32(0, 0, 0, 255);
    }

    private static bool IsInColorRange(Rgba32 pixel, Rgba32 min, Rgba32 max)
    {
        return pixel.R >= min.R && pixel.R <= max.R &&
               pixel.G >= min.G && pixel.G <= max.G &&
               pixel.B >= min.B && pixel.B <= max.B;
    }
}

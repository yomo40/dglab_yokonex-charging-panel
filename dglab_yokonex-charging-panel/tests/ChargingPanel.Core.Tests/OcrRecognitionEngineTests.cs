using ChargingPanel.Core.OCR;
using ChargingPanel.Core.OCR.Recognition;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class OcrRecognitionEngineTests
{
    [Fact]
    public void OcrColorRecognitionEngine_ShouldEstimateFillByLength()
    {
        using var image = new Image<Rgba32>(100, 20, new Rgba32(20, 20, 20, 255));
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < 60; x++)
            {
                image[x, y] = new Rgba32(220, 30, 30, 255);
            }
        }

        // 注入稀疏噪声：不应把边界误判到满血。
        for (var y = 0; y < image.Height; y += 3)
        {
            for (var x = 90; x < 99; x++)
            {
                image[x, y] = new Rgba32(220, 30, 30, 255);
            }
        }

        var config = new OCRConfig
        {
            HealthColorMin = "#A00000",
            HealthColorMax = "#FF5555",
            BackgroundColorMin = "#000000",
            BackgroundColorMax = "#444444"
        };

        var engine = new OcrColorRecognitionEngine();
        var result = engine.AnalyzeHealthBar(image, config);

        Assert.True(result.Success);
        Assert.InRange(result.HealthPercent, 55, 65);
    }

    [Fact]
    public void OcrDigitRecognitionEngine_ShouldRecognizeSyntheticDigits()
    {
        using var image = CreateDigitImage("75", scale: 4);
        var engine = new OcrDigitRecognitionEngine();

        var ok = engine.TryAnalyzeHealthPercent(image, out var result);

        Assert.True(ok);
        Assert.True(result.Success);
        Assert.Equal(75, result.NumericValue);
        Assert.Equal(75, result.HealthPercent);
        Assert.True(result.Confidence > 0.6);
    }

    [Fact]
    public void OcrDigitRecognitionEngine_ShouldKeepAbsoluteValue_WhenGreaterThan100()
    {
        using var image = CreateDigitImage("200", scale: 4);
        var engine = new OcrDigitRecognitionEngine();

        var ok = engine.TryAnalyzeHealthPercent(image, out var result);

        Assert.True(ok);
        Assert.True(result.Success);
        Assert.Equal(200, result.NumericValue);
        Assert.Equal(100, result.HealthPercent);
    }

    private static Image<Rgba32> CreateDigitImage(string digits, int scale)
    {
        var templates = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "11111", "10001", "10001", "10001", "10001", "10001", "11111" },
            ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
            ['2'] = new[] { "11111", "00001", "00001", "11111", "10000", "10000", "11111" },
            ['3'] = new[] { "11111", "00001", "00001", "01111", "00001", "00001", "11111" },
            ['4'] = new[] { "10001", "10001", "10001", "11111", "00001", "00001", "00001" },
            ['5'] = new[] { "11111", "10000", "10000", "11111", "00001", "00001", "11111" },
            ['6'] = new[] { "11111", "10000", "10000", "11111", "10001", "10001", "11111" },
            ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
            ['8'] = new[] { "11111", "10001", "10001", "11111", "10001", "10001", "11111" },
            ['9'] = new[] { "11111", "10001", "10001", "11111", "00001", "00001", "11111" }
        };

        var digitWidth = 5 * scale;
        var digitHeight = 7 * scale;
        var spacing = 2 * scale;
        var margin = 2 * scale;
        var width = margin * 2 + digits.Length * digitWidth + (digits.Length - 1) * spacing;
        var height = margin * 2 + digitHeight;

        var image = new Image<Rgba32>(width, height, new Rgba32(8, 8, 8, 255));
        for (var i = 0; i < digits.Length; i++)
        {
            var ch = digits[i];
            if (!templates.TryGetValue(ch, out var rows))
            {
                continue;
            }

            var startX = margin + i * (digitWidth + spacing);
            var startY = margin;
            for (var row = 0; row < rows.Length; row++)
            {
                var pattern = rows[row];
                for (var col = 0; col < pattern.Length; col++)
                {
                    if (pattern[col] != '1')
                    {
                        continue;
                    }

                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            image[startX + col * scale + dx, startY + row * scale + dy] = new Rgba32(240, 240, 240, 255);
                        }
                    }
                }
            }
        }

        return image;
    }
}

using System;
using System.IO;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChargingPanel.Core.Utils;

/// <summary>
/// QR 码生成工具
/// </summary>
public static class QRCodeHelper
{
    /// <summary>
    /// 生成 QR 码图像字节数组 (PNG 格式)
    /// </summary>
    /// <param name="content">QR 码内容</param>
    /// <param name="pixelsPerModule">每个模块的像素数（默认 10）</param>
    /// <returns>PNG 图像字节数组</returns>
    public static byte[] GeneratePng(string content, int pixelsPerModule = 10)
    {
        using var qrGenerator = new QRCoder.QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// 生成 QR 码并保存到文件
    /// </summary>
    /// <param name="content">QR 码内容</param>
    /// <param name="filePath">保存路径</param>
    /// <param name="pixelsPerModule">每个模块的像素数</param>
    public static void SaveToFile(string content, string filePath, int pixelsPerModule = 10)
    {
        var pngBytes = GeneratePng(content, pixelsPerModule);
        File.WriteAllBytes(filePath, pngBytes);
    }

    /// <summary>
    /// 生成 QR 码的 Base64 字符串（用于 HTML img src）
    /// </summary>
    /// <param name="content">QR 码内容</param>
    /// <param name="pixelsPerModule">每个模块的像素数</param>
    /// <returns>Base64 编码的 PNG 图像</returns>
    public static string GenerateBase64(string content, int pixelsPerModule = 10)
    {
        var pngBytes = GeneratePng(content, pixelsPerModule);
        return Convert.ToBase64String(pngBytes);
    }

    /// <summary>
    /// 生成 QR 码的 Data URI（用于 HTML img src）
    /// </summary>
    /// <param name="content">QR 码内容</param>
    /// <param name="pixelsPerModule">每个模块的像素数</param>
    /// <returns>Data URI 格式的图像</returns>
    public static string GenerateDataUri(string content, int pixelsPerModule = 10)
    {
        var base64 = GenerateBase64(content, pixelsPerModule);
        return $"data:image/png;base64,{base64}";
    }
}

namespace ChargingPanel.Core.OCR.Region;

/// <summary>
/// OCR 区域管理器：统一区域有效性判断。
/// </summary>
internal sealed class OcrRegionManager
{
    public bool IsHealthRegionValid(OCRConfig config)
    {
        return IsRegionValid(config.Area);
    }

    public bool IsArmorRegionValid(OCRConfig config)
    {
        return config.ArmorEnabled && IsRegionValid(config.ArmorArea);
    }

    public bool IsRegionValid(OCRArea area)
    {
        return area.Width >= 10 &&
               area.Height >= 5 &&
               area.X >= 0 &&
               area.Y >= 0;
    }
}


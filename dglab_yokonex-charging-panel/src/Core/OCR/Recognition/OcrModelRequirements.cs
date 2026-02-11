using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChargingPanel.Core.OCR.Recognition;

/// <summary>
/// OCR 模型规范与校验。
/// </summary>
public static class OcrModelRequirements
{
    public static readonly string[] RequiredOutputs =
    {
        "hp_percent",
        "ahp_percent",
        "shield_level",
        "alive_state",
        "round_state"
    };

    public static readonly string[] AcceptedInputNames =
    {
        "input"
    };

    public const string ExpectedInputShape = "[1,3,64,128]";

    public static OcrModelValidationResult ValidatePath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return OcrModelValidationResult.Invalid("modelPath is empty");
        }

        var fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
        {
            return OcrModelValidationResult.Invalid($"model file not found: {fullPath}");
        }

        if (!fullPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return OcrModelValidationResult.Invalid("model file extension must be .onnx");
        }

        return OcrModelValidationResult.Valid(fullPath);
    }

    public static OcrModelValidationResult ValidateMetadata(
        IEnumerable<string> inputNames,
        IEnumerable<string> outputNames)
    {
        var normalizedInputs = inputNames.Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedOutputs = outputNames.Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!AcceptedInputNames.All(normalizedInputs.Contains))
        {
            return OcrModelValidationResult.Invalid($"missing required input(s): {string.Join(",", AcceptedInputNames.Where(x => !normalizedInputs.Contains(x)))}");
        }

        var missingOutputs = RequiredOutputs.Where(x => !normalizedOutputs.Contains(x)).ToArray();
        if (missingOutputs.Length > 0)
        {
            return OcrModelValidationResult.Invalid($"missing required output(s): {string.Join(",", missingOutputs)}");
        }

        return OcrModelValidationResult.Valid();
    }
}

public sealed class OcrModelValidationResult
{
    private OcrModelValidationResult(bool isValid, string? message, string? fullPath)
    {
        IsValid = isValid;
        Message = message;
        FullPath = fullPath;
    }

    public bool IsValid { get; }
    public string? Message { get; }
    public string? FullPath { get; }

    public static OcrModelValidationResult Valid(string? fullPath = null) => new(true, null, fullPath);
    public static OcrModelValidationResult Invalid(string message) => new(false, message, null);
}


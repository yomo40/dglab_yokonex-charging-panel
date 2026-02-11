using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ChargingPanel.Core.OCR;

/// <summary>
/// ONNX OCR 引擎 - 支持 DirectML (NVIDIA/AMD/Intel GPU)
/// 
/// 模型输入: [batch, 3, 64, 128] RGB图像
/// 模型输出:
///   - hp_percent: [batch, 1] 血量百分比 (0-1)
///   - ahp_percent: [batch, 1] 护甲百分比 (0-1)
///   - shield_level: [batch, 6] 护甲等级logits
///   - alive_state: [batch, 3] 存活状态logits (0=alive, 1=dead, 2=knocked)
///   - round_state: [batch, 3] 回合状态logits (0=playing, 1=new_round, 2=game_over)
/// </summary>
public class OnnxOCREngine : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<OnnxOCREngine>();
    private InferenceSession? _session;
    private bool _isInitialized;
    private string? _modelPath;
    
    // 模型输入尺寸
    private const int InputWidth = 128;
    private const int InputHeight = 64;
    
    // ImageNet 归一化参数
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    
    // 状态映射
    public static readonly string[] ShieldLevels = { "none", "white", "blue", "purple", "gold", "red" };
    public static readonly string[] AliveStates = { "alive", "dead", "knocked" };
    public static readonly string[] RoundStates = { "playing", "new_round", "game_over" };

    /// <summary>
    /// 是否已初始化
    /// </summary>
    public bool IsInitialized => _isInitialized;
    
    /// <summary>
    /// 当前模型路径
    /// </summary>
    public string? ModelPath => _modelPath;

    /// <summary>
    /// 当前模型的输入/输出名称。
    /// </summary>
    public (IReadOnlyCollection<string> Inputs, IReadOnlyCollection<string> Outputs) GetModelIoNames()
    {
        if (_session == null)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        return (
            _session.InputMetadata.Keys.ToArray(),
            _session.OutputMetadata.Keys.ToArray());
    }

    /// <summary>
    /// 初始化 ONNX 引擎
    /// </summary>
    /// <param name="modelPath">ONNX 模型路径</param>
    /// <param name="useGpu">是否使用 GPU</param>
    /// <param name="gpuDeviceId">GPU 设备 ID</param>
    public void Initialize(string modelPath, bool useGpu = true, int gpuDeviceId = 0)
    {
        if (_isInitialized && _modelPath == modelPath)
        {
            _logger.Debug("Model already loaded: {Path}", modelPath);
            return;
        }
        
        Dispose();
        
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model file not found: {modelPath}");
        }
        
        _logger.Information("Initializing ONNX engine: {Path}, GPU={UseGpu}, DeviceId={DeviceId}", 
            modelPath, useGpu, gpuDeviceId);
        
        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        if (useGpu)
        {
            try
            {
                // DirectML 支持 NVIDIA/AMD/Intel GPU
                options.AppendExecutionProvider_DML(gpuDeviceId);
                _logger.Information("DirectML GPU acceleration enabled (Device {Id})", gpuDeviceId);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to enable DirectML, falling back to CPU");
            }
        }
        
        _session = new InferenceSession(modelPath, options);
        _modelPath = modelPath;
        _isInitialized = true;
        
        // 打印模型信息
        LogModelInfo();
    }
    
    private void LogModelInfo()
    {
        if (_session == null) return;
        
        _logger.Information("Model loaded successfully");
        _logger.Debug("Inputs: {Inputs}", string.Join(", ", 
            _session.InputMetadata.Select(x => $"{x.Key}:{string.Join("x", x.Value.Dimensions)}")));
        _logger.Debug("Outputs: {Outputs}", string.Join(", ", 
            _session.OutputMetadata.Select(x => $"{x.Key}:{string.Join("x", x.Value.Dimensions)}")));
    }

    /// <summary>
    /// 识别图像
    /// </summary>
    public OnnxOCRResult? Recognize(Image<Rgba32> image)
    {
        if (!_isInitialized || _session == null)
        {
            _logger.Warning("ONNX engine not initialized");
            return null;
        }
        
        try
        {
            // 预处理图像
            var tensor = PreprocessImage(image);
            
            // 运行推理
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", tensor)
            };
            
            using var results = _session.Run(inputs);
            
            // 解析输出
            return ParseResults(results);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Recognition failed");
            return null;
        }
    }
    
    /// <summary>
    /// 预处理图像: Resize + Normalize
    /// </summary>
    private DenseTensor<float> PreprocessImage(Image<Rgba32> image)
    {
        // 克隆并调整大小
        using var resized = image.Clone(ctx => ctx.Resize(InputWidth, InputHeight));
        
        // 创建张量 [1, 3, H, W]
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputHeight, InputWidth });
        
        // 填充数据并归一化
        for (int y = 0; y < InputHeight; y++)
        {
            for (int x = 0; x < InputWidth; x++)
            {
                var pixel = resized[x, y];
                tensor[0, 0, y, x] = (pixel.R / 255f - Mean[0]) / Std[0];  // R
                tensor[0, 1, y, x] = (pixel.G / 255f - Mean[1]) / Std[1];  // G
                tensor[0, 2, y, x] = (pixel.B / 255f - Mean[2]) / Std[2];  // B
            }
        }
        
        return tensor;
    }
    
    /// <summary>
    /// 解析模型输出
    /// </summary>
    private OnnxOCRResult ParseResults(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var result = new OnnxOCRResult();
        
        foreach (var output in results)
        {
            var data = output.AsEnumerable<float>().ToArray();
            
            switch (output.Name)
            {
                case "hp_percent":
                    result.HpPercent = Math.Clamp(data[0], 0f, 1f);
                    result.Blood = (int)(result.HpPercent * 100);
                    break;
                    
                case "ahp_percent":
                    result.AhpPercent = Math.Clamp(data[0], 0f, 1f);
                    result.Armor = (int)(result.AhpPercent * 100);
                    break;
                    
                case "shield_level":
                    result.ShieldLevelIndex = ArgMax(data);
                    result.ShieldLevel = ShieldLevels[result.ShieldLevelIndex];
                    break;
                    
                case "alive_state":
                    result.AliveStateIndex = ArgMax(data);
                    result.AliveState = AliveStates[result.AliveStateIndex];
                    break;
                    
                case "round_state":
                    result.RoundStateIndex = ArgMax(data);
                    result.RoundState = RoundStates[result.RoundStateIndex];
                    break;
            }
        }
        
        result.Success = true;
        return result;
    }
    
    private static int ArgMax(float[] arr)
    {
        int idx = 0;
        for (int i = 1; i < arr.Length; i++)
        {
            if (arr[i] > arr[idx]) idx = i;
        }
        return idx;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _isInitialized = false;
        _modelPath = null;
    }
}

/// <summary>
/// ONNX OCR 识别结果
/// </summary>
public class OnnxOCRResult
{
    public bool Success { get; set; }
    
    // 血量 (兼容 OCRResult)
    public int Blood { get; set; }
    public float HpPercent { get; set; }
    
    // 护甲 (兼容 OCRResult)
    public int Armor { get; set; }
    public float AhpPercent { get; set; }
    
    // 护甲等级
    public int ShieldLevelIndex { get; set; }
    public string ShieldLevel { get; set; } = "none";
    
    // 存活状态: 0=alive, 1=dead, 2=knocked
    public int AliveStateIndex { get; set; }
    public string AliveState { get; set; } = "alive";
    
    // 回合状态: 0=playing, 1=new_round, 2=game_over
    public int RoundStateIndex { get; set; }
    public string RoundState { get; set; } = "playing";
    
    /// <summary>
    /// 是否死亡
    /// </summary>
    public bool IsDead => AliveStateIndex == 1;
    
    /// <summary>
    /// 是否倒地
    /// </summary>
    public bool IsKnocked => AliveStateIndex == 2;
    
    /// <summary>
    /// 是否新回合
    /// </summary>
    public bool IsNewRound => RoundStateIndex == 1;
    
    /// <summary>
    /// 是否游戏结束
    /// </summary>
    public bool IsGameOver => RoundStateIndex == 2;
}

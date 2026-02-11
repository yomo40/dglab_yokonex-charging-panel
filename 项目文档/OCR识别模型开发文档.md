# OCR 识别模型开发文档（开发者版）

> 更新时间：2026-02-11  
> 适用版本: v0.9.3~new
> 当前 `OCRService + OnnxOCREngine + OcrModelRequirements` 实现

## 1. 文档目标

本文面向模型开发者，解决两个问题：

1. 如何为本程序训练可用 OCR 模型。  
2. 如何让模型按程序要求导出并成功接入。

## 2. OCR 模块工作方式

程序支持四种识别模式（`OCRConfig.Mode`）：

- `auto`：血条识别 + 数字识别融合（默认）。
- `healthbar`：仅颜色血条识别。
- `digital`：优先数字识别，失败回退血条识别。
- `model`：优先 ONNX 模型，失败回退内置识别。

在 `model` 模式下，流程是：

```text
截图区域 -> Resize(128x64) -> RGB归一化 -> ONNX推理 -> 结果映射
```

## 3. 你要训练什么输出

程序对模型输出有固定要求。

### 3.1 必需输入

- 输入名必须包含：`input`
- 期望输入形状：`[1,3,64,128]`

### 3.2 必需输出

必须包含以下输出名：

- `hp_percent`：血量百分比，范围建议 `0~1`
- `ahp_percent`：护甲百分比，范围建议 `0~1`
- `shield_level`：护甲等级 logits，长度 `6`
- `alive_state`：生存状态 logits，长度 `3`
- `round_state`：回合状态 logits，长度 `3`

程序对分类输出使用 `argmax`：

- `alive_state`: `0=alive`, `1=dead`, `2=knocked`
- `round_state`: `0=playing`, `1=new_round`, `2=game_over`

## 4. 数据集标注建议

建议每条样本包含以下标签：

- `hp_percent`（float）
- `ahp_percent`（float）
- `shield_level`（int: 0~5）
- `alive_state`（int: 0~2）
- `round_state`（int: 0~2）

训练集要覆盖：

- 不同分辨率与 UI 缩放。
- 白天/夜晚、特效、受击闪屏。
- HUD 被遮挡、模糊、压缩噪声。
- 极端状态（0血、满血、回合切换）。

## 5. 预处理必须对齐程序

程序推理前做了固定预处理，你的训练也应一致：

1. 输入图像缩放到 `128x64`。  
2. RGB 三通道。  
3. 归一化：
   - mean = `[0.485, 0.456, 0.406]`
   - std  = `[0.229, 0.224, 0.225]`

如果训练预处理不一致，线上精度会明显下降。

## 6. 训练与导出建议

## 6.1 模型结构建议

建议多任务网络（共享 backbone + 多个 head）：

- 回归 head：`hp_percent`、`ahp_percent`
- 分类 head：`shield_level`、`alive_state`、`round_state`

## 6.2 损失函数建议

- 百分比回归：`MSE` 或 `SmoothL1`
- 状态分类：`CrossEntropy`
- 多任务总损失：加权求和

## 6.3 导出 ONNX

导出时必须保证输入/输出名字严格匹配：

- input: `input`
- outputs: `hp_percent`, `ahp_percent`, `shield_level`, `alive_state`, `round_state`

示例：

```python
torch.onnx.export(
    model,
    dummy_input,                  # shape [1,3,64,128]
    "ocr_model.onnx",
    input_names=["input"],
    output_names=[
        "hp_percent",
        "ahp_percent",
        "shield_level",
        "alive_state",
        "round_state"
    ],
    opset_version=17,
    dynamic_axes=None
)
```

## 7. 程序侧接入步骤

1. 在 OCR 页面将模式改为 `model`。  
2. 选择 `.onnx` 文件路径。  
3. 选择 `UseGPU` 与 `GPUDeviceId`（DirectML）。  
4. 点击单次识别测试。  
5. 观察日志是否出现以下错误：
   - `modelPath is empty`
   - `model file not found`
   - `model file extension must be .onnx`
   - `missing required input(s)`
   - `missing required output(s)`

如果模型验证失败，程序会自动回退内置识别模式，不会中断主流程。



## 9. 当前边界

- 训练流程与标注工具不内置在本项目，需要外部训练工程完成。  
- 程序当前只负责：模型加载、元数据校验、推理执行、失败回退、事件上报。


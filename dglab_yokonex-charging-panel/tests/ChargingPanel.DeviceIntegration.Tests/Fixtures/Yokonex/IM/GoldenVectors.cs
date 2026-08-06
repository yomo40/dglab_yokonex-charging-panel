namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex.IM;

/// <summary>
/// 役次元 IM 协议黄金载荷（来自官方 README / ycy_im_demo.ts）。
/// </summary>
internal static class GoldenVectors
{
    public const string ConnectCode = "5 token_value";

    public const string LoginSubject = "game_5";

    public const string TargetUserId = "5";

    public const string GameCmd = "{\"code\":\"game_cmd\",\"id\":\"hurt\"}";

    public const string GameCmdWithToken = "{\"code\":\"game_cmd\",\"id\":\"hurt\",\"token\":\"token_value\"}";

    public const string GlobalStop = "{\"code\":\"game_cmd\",\"id\":\"_stop_all\"}";

    public const string LegacyGameInfo = "{\"code\":\"game_info\",\"data\":1}";
}

using System;

namespace ChargingPanel.Core.Network;

/// <summary>
/// UDP 通道标准协议模板，供 MOD 开发者按统一格式接入。
/// </summary>
public static class ModBridgeUdpProtocol
{
    public const string Version = "1.0";

    public static object BuildTemplateResponse(int port)
    {
        return new
        {
            type = "template",
            transport = "udp",
            version = Version,
            listen = $"udp://127.0.0.1:{port}",
            flow = new[] { "hello", "event", "heartbeat", "goodbye" },
            hello = new
            {
                type = "hello",
                scriptId = "script_id_required",
                name = "mod_name",
                game = "game_name",
                version = "1.0.0",
                rules = new[]
                {
                    new
                    {
                        eventId = "boss-hit",
                        action = "set",
                        channel = "A",
                        value = 20,
                        duration = 500,
                        triggerType = "value-decrease",
                        minChange = 5,
                        condition = new
                        {
                            field = "newValue",
                            @operator = "<=",
                            value = 30
                        }
                    }
                }
            },
            @event = new
            {
                type = "event",
                sessionId = "from_welcome",
                scriptId = "optional_if_session_exists",
                eventId = "your_event_id",
                gameEventType = "Custom",
                oldValue = 0,
                newValue = 1,
                data = new { change = 1 }
            },
            heartbeat = new
            {
                type = "heartbeat",
                sessionId = "from_welcome"
            },
            goodbye = new
            {
                type = "goodbye",
                sessionId = "from_welcome"
            }
        };
    }
}

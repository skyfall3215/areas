using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Network;

public sealed class DiscoveryBeacon
{
    public const string DefaultMagic = "SECOND_SCREEN_DISCOVERY";

    [JsonPropertyName("magic")]
    public string Magic { get; set; } = DefaultMagic;

    [JsonPropertyName("version")]
    public int Version { get; set; } = ProtocolConstants.Version1;

    [JsonPropertyName("hostname")]
    public string HostName { get; set; } = Environment.MachineName;

    [JsonPropertyName("port")]
    public int Port { get; set; } = ProtocolConstants.DefaultPort;

    [JsonPropertyName("addresses")]
    public List<string> Addresses { get; set; } = new();

    public byte[] ToBytes()
    {
        string json = JsonSerializer.Serialize(this);
        return Encoding.UTF8.GetBytes(json);
    }

    public static DiscoveryBeacon? TryParse(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        return TryParse(Encoding.UTF8.GetString(data));
    }

    public static DiscoveryBeacon? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Try JSON format
        try
        {
            var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(text);
            if (beacon != null && beacon.Magic == DefaultMagic)
            {
                return beacon;
            }
        }
        catch
        {
            // Not valid JSON, try text format below
        }

        // Try plain text format: SECOND_SCREEN_HOST:<port>:<hostname>
        if (text.StartsWith("SECOND_SCREEN_HOST:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("SECOND_SCREEN_DISCOVERY:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(':');
            if (parts.Length >= 3 && int.TryParse(parts[1], out int port))
            {
                return new DiscoveryBeacon
                {
                    Magic = DefaultMagic,
                    Version = ProtocolConstants.Version1,
                    Port = port,
                    HostName = parts[2].Trim()
                };
            }
        }

        return null;
    }
}


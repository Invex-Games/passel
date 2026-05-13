namespace Invex.Passel.WebApi.Common;

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter<PacketType>))]
public enum PacketType
{
    [JsonStringEnumMemberName("link")] Link,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("file")] File,
}

using System.Text.Json.Serialization;

namespace WhatsBox;

/// <summary>
/// Source-generated JSON contract for PRODUCT.md v1 RPC params/results and chat events.
/// Use <see cref="Default"/> or <see cref="JsonRpc.SerializerOptions"/> — do not rely on reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = false,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(InitializeOptions))]
[JsonSerializable(typeof(DirectoryListOptions))]
[JsonSerializable(typeof(DirectoryGetParams))]
[JsonSerializable(typeof(TopicsParams))]
[JsonSerializable(typeof(MessagesSendParams))]
[JsonSerializable(typeof(MessagesReadParams))]
[JsonSerializable(typeof(MessageReply))]
[JsonSerializable(typeof(MessageReact))]
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(TopicsResult))]
[JsonSerializable(typeof(DirectoryListResult))]
[JsonSerializable(typeof(DirectoryRow))]
[JsonSerializable(typeof(DirectoryParticipant))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(ReadResult))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatText))]
[JsonSerializable(typeof(ChatImage))]
[JsonSerializable(typeof(ChatVideo))]
[JsonSerializable(typeof(ChatAudio))]
[JsonSerializable(typeof(ChatDocument))]
[JsonSerializable(typeof(ChatSticker))]
[JsonSerializable(typeof(ChatLocation))]
[JsonSerializable(typeof(ChatReaction))]
[JsonSerializable(typeof(ChatAck))]
[JsonSerializable(typeof(ChatMeta))]
[JsonSerializable(typeof(ChatUnknown))]
public sealed partial class WhatsJsonContext : JsonSerializerContext;

using System.Text.Json.Serialization;

namespace Inbox;

/// <summary>
/// Source-generated JSON contract for INBOX.md v1 RPC params/results and chat events.
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
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(TopicsResult))]
[JsonSerializable(typeof(DirectoryListResult))]
[JsonSerializable(typeof(DirectoryRow))]
[JsonSerializable(typeof(DirectoryParticipant))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(ReadResult))]
[JsonSerializable(typeof(ChatEvent))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatReaction))]
[JsonSerializable(typeof(ChatAck))]
[JsonSerializable(typeof(ChatMeta))]
[JsonSerializable(typeof(ContentPart))]
[JsonSerializable(typeof(TextPart))]
[JsonSerializable(typeof(ImagePart))]
[JsonSerializable(typeof(VideoPart))]
[JsonSerializable(typeof(AudioPart))]
[JsonSerializable(typeof(DocumentPart))]
[JsonSerializable(typeof(StickerPart))]
[JsonSerializable(typeof(LocationPart))]
[JsonSerializable(typeof(UnknownPart))]
[JsonSerializable(typeof(ReactionPart))]
[JsonSerializable(typeof(AckPart))]
[JsonSerializable(typeof(MetaPart))]
[JsonSerializable(typeof(IReadOnlyList<ContentPart>))]
public sealed partial class InboxJsonContext : JsonSerializerContext;


using System.Text.Json.Serialization;

namespace Inbox;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcRequest<InitializeOptions>))]
[JsonSerializable(typeof(JsonRpcRequest<TopicsParams>))]
[JsonSerializable(typeof(JsonRpcRequest<DirectoryListOptions>))]
[JsonSerializable(typeof(JsonRpcRequest<DirectoryGetParams>))]
[JsonSerializable(typeof(JsonRpcRequest<MessagesSendParams>))]
[JsonSerializable(typeof(JsonRpcRequest<MessagesReadParams>))]
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
sealed partial class JsonRpcContext : JsonSerializerContext;


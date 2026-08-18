using System.Text.Json.Serialization;

namespace WhatsBox;

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
sealed partial class JsonRpcContext : JsonSerializerContext;

namespace WhatsDemo;

/// <summary>One popup row: <see cref="Label"/> is shown, <see cref="Insert"/> is written into the buffer.</summary>
public readonly record struct CompletionItem(string Insert, string Label);

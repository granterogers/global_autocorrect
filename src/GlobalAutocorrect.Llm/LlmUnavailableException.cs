namespace GlobalAutocorrect.Llm;

/// <summary>The local model server could not be reached or gave an unusable reply (10.5). Never carries typed text (8.6).</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Api.Services;

// Only unambiguous small talk is gated deterministically (it skips the intent router
// and the agent loop entirely). Whether any other message wants a draft or an English
// answer is decided by the model (AiFlowBuilderService.IsFlowRequestAsync), never by
// word lists.
public static class ChatIntentGate
{
    private static readonly HashSet<string> CasualPhrases = new(StringComparer.Ordinal)
    {
        "hi", "hello", "hey", "yo", "hiya", "howdy", "sup",
        "good morning", "good afternoon", "good evening", "good night",
        "morning", "evening",
        "how are you", "how are you doing", "how r u", "how's it going", "hows it going",
        "how do you do", "how have you been", "are you ok", "are you okay", "you ok",
        "who are you", "what are you", "what can you do", "what do you do",
        "tell me about yourself", "introduce yourself", "what can you help me with",
        "thanks", "thank you", "thx", "ty", "cheers",
        "bye", "goodbye", "see you",
        "ok", "okay", "got it", "understood", "nice", "great", "cool", "awesome"
    };

    public static bool IsCasual(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().TrimEnd('!', '?', '.', ',', '~', ' ').Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 60) return false;
        return CasualPhrases.Contains(normalized);
    }
}

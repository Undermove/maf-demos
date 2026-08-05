using System.Text;
using System.Text.Json;

namespace D_CountryPipelineDemo;

/// <summary>
/// Канал связи с человеком. Если задан Telegram:BotToken — вопросы и подтверждения уходят
/// в Telegram (как в проде — в корпоративный мессенджер); иначе fallback в консоль,
/// чтобы демо работало и без сети до Telegram.
/// </summary>
public sealed class MessengerTools(string? botToken, string? chatId)
{
    private readonly HttpClient _http = new();
    private long _lastUpdateId;
    public bool TelegramEnabled => !string.IsNullOrWhiteSpace(botToken) && !string.IsNullOrWhiteSpace(chatId);

    /// <summary>Свободный вопрос человеку: отправить и ждать текстового ответа.</summary>
    public async Task<string> AskAsync(string question)
    {
        if (!TelegramEnabled)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"\n💬 Вопрос от агента: {question}\n   Твой ответ: ");
            Console.ResetColor();
            return Console.ReadLine() ?? "";
        }

        await SkipPendingUpdatesAsync();
        await PostAsync("sendMessage", new { chat_id = chatId, text = $"💬 {question}" });
        Console.WriteLine("   ⏳ вопрос отправлен в Telegram, жду ответа…");

        while (true)
        {
            foreach (var update in await GetUpdatesAsync())
                if (update.TryGetProperty("message", out var msg) &&
                    msg.GetProperty("chat").GetProperty("id").ToString() == chatId &&
                    msg.TryGetProperty("text", out var text))
                    return text.GetString() ?? "";
        }
    }

    /// <summary>Подтверждение действия: инлайн-кнопки в Telegram или [y/N] в консоли.</summary>
    public async Task<bool> ApproveAsync(string action)
    {
        if (!TelegramEnabled)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"\n⚠ Агент хочет: {action}\n  Разрешить? [y/N] ");
            Console.ResetColor();
            return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "д" or "да";
        }

        await SkipPendingUpdatesAsync();
        await PostAsync("sendMessage", new
        {
            chat_id = chatId,
            text = $"⚠ Агент хочет: {action}\nРазрешить?",
            reply_markup = new
            {
                inline_keyboard = new[] { new[]
                {
                    new { text = "✅ Да", callback_data = "yes" },
                    new { text = "❌ Нет", callback_data = "no" },
                } },
            },
        });
        Console.WriteLine("   ⏳ запрос на подтверждение отправлен в Telegram, жду…");

        while (true)
        {
            foreach (var update in await GetUpdatesAsync())
                if (update.TryGetProperty("callback_query", out var cb))
                {
                    var approved = cb.GetProperty("data").GetString() == "yes";
                    await PostAsync("answerCallbackQuery", new { callback_query_id = cb.GetProperty("id").GetString() });
                    await PostAsync("sendMessage", new { chat_id = chatId, text = approved ? "→ одобрено ✅" : "→ отклонено ❌" });
                    return approved;
                }
        }
    }

    private async Task SkipPendingUpdatesAsync()
    {
        foreach (var update in await GetUpdatesAsync(timeoutSeconds: 0)) { } // просто сдвигаем offset
    }

    private async Task<List<JsonElement>> GetUpdatesAsync(int timeoutSeconds = 25)
    {
        var response = await _http.GetStringAsync(
            $"https://api.telegram.org/bot{botToken}/getUpdates?timeout={timeoutSeconds}&offset={_lastUpdateId + 1}");
        var updates = JsonDocument.Parse(response).RootElement.GetProperty("result").EnumerateArray().ToList();
        foreach (var u in updates)
            _lastUpdateId = Math.Max(_lastUpdateId, u.GetProperty("update_id").GetInt64());
        return updates;
    }

    private async Task PostAsync(string method, object payload)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"https://api.telegram.org/bot{botToken}/{method}", content);
        response.EnsureSuccessStatusCode();
    }
}

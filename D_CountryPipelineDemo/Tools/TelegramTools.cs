using System.ComponentModel;

namespace D_CountryPipelineDemo;

/// <summary>
/// Тул общения с человеком: агент задаёт вопрос менеджеру страны и блокируется до ответа.
/// Канал — Telegram (или консоль, если Telegram не настроен), см. <see cref="MessengerTools"/>.
/// </summary>
public sealed class TelegramTools(MessengerTools messengerTools)
{
    [Description("Ask the country manager a question and wait for the answer")]
    public async Task<string> AskManager(
        [Description("Question text, in Russian")] string question)
    {
        var answer = await messengerTools.AskAsync(question);
        Console.WriteLine($"   ✉ ответ менеджера: {answer}");
        return answer;
    }
}

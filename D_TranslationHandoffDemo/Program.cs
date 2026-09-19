using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

#region config

var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

OpenAIClient openAi = new OpenAIClient(
    new ApiKeyCredential(config["OpenAI:ApiKey"]
                         ?? throw new InvalidOperationException("Нет OpenAI:ApiKey — скопируй appsettings.local.json.example → appsettings.local.json")),
    new OpenAIClientOptions { Endpoint = new Uri(config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1") });

var miniModel = config["OpenAI:MiniModel"] ?? "gpt-4o-mini";
var strongModel = config["OpenAI:StrongModel"] ?? "gpt-4o";
#endregion

// gpt-4o-mini,
IChatClient miniClient = openAi.GetChatClient(miniModel).AsIChatClient();

// gpt-4o
IChatClient strongClient = openAi.GetChatClient(strongModel).AsIChatClient();

// Диспетчер на малой модели. Список «плохих» языков НЕ зашит: модель сама оценивает,
// потянет ли она этот язык. Где проходит граница — свойство конкретной модели, а не факт
// про мир: у следующей версии она сдвинется, и хардкод протухнет.
AIAgent dispatcher = new ChatClientAgent(
    miniClient,
    name: "gpt-4o-mini",
    instructions: "You translate the user's text into the requested target language and output ONLY the translation. " +
                  "You are strong at widely-spoken languages — translate those yourself, do not hand off. " +
                  "Hand off to expert_translator ONLY if the target language is one you genuinely handle poorly, " +
                  "where your output would be clumsy or transliterated instead of idiomatic.");

// Эксперт на старшей модели. Temperature = 0 — на сцене каждый прогон одинаково хороший.
AIAgent expert = new ChatClientAgent(
    strongClient,
    name: "gpt-4o",
    instructions: "You are an expert translator for low-resource languages. " +
                 "Translate accurately and idiomatically, never transliterate. Output ONLY the translation.");

// ВЕСЬ воркфлоу: готовая handoff-оркестрация из AgentWorkflowBuilder.
Workflow workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(dispatcher)
    .WithHandoff(from: dispatcher, to: expert, "The dispatcher is not confident it can translate into this language well")
    .Build();

// Воркфлоу — это тоже агент: дальше запускаем его как обычного агента, без TurnToken и событий.
AIAgent translator = workflow.AsAIAgent();

// Тот же текст-ловушка, что в BadTranslationDemo: бренд, англицизмы, идиома.
const string announcement = "Встречайте Додстер-комбо: кешбэк додокоинами, лимитированный мерч и пицца, " +
                            "которая разлетается, как горячие пирожки!";

Console.WriteLine($"Диспетчер: {miniModel}, эксперт: {strongModel}");
Console.WriteLine($"Анонс (ru): {announcement}");

// 1) Большой язык — диспетчер переводит сам.
await TranslateAsync("English", "en");

// 2) Малый язык — диспетчер передаёт управление эксперту (хендофф).
await TranslateAsync("Georgian", "ka");

return;

// Стримит ответ воркфлоу-агента в консоль (видно, кто именно отвечает).
async Task TranslateAsync(string name, string code)
{
    Console.WriteLine($"\n--- Переводим на {name} ({code}) ---");

    string? speaking = null;
    await foreach (var update in translator.RunStreamingAsync($"Translate into {name} ({code}): {announcement}"))
    {
        if (string.IsNullOrEmpty(update.Text)) continue;

        if (speaking != update.AuthorName)
        {
            speaking = update.AuthorName;
            Console.Write($"\n{speaking}: ");
        }
        Console.Write(update.Text);
    }
    Console.WriteLine();
}
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

#region config

var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var openAi = new OpenAIClient(
    new ApiKeyCredential(config["OpenAI:ApiKey"]
                         ?? throw new InvalidOperationException("Нет OpenAI:ApiKey — скопируй appsettings.local.json.example → appsettings.local.json")),
    new OpenAIClientOptions { Endpoint = new Uri(config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1") });

var miniModel = config["OpenAI:MiniModel"] ?? "gpt-4o-mini";
var strongModel = config["OpenAI:StrongModel"] ?? "gpt-4o";
#endregion

// gpt-4o-mini,
IChatClient mini = openAi.GetChatClient(miniModel).AsIChatClient();

// gpt-4o
IChatClient strong = openAi.GetChatClient(strongModel).AsIChatClient();

// Сложный для перевода текст: имя собственное + англицизмы + идиома
const string text = "Встречайте Додстер-комбо: кешбэк додокоинами, лимитированный мерч и пицца, " +
                    "которая разлетается, как горячие пирожки!";

const string translatePrompt = "You are a professional translator. Translate the following text into Georgian. " +
                               $"Never transliterate. Output ONLY the translation.\n\n{text}";

Console.WriteLine($"Оригинал (ru): {text}");

// 1) Малая модель переводит на грузинский.
var miniKa = await mini.GetResponseAsync(translatePrompt);

Console.WriteLine($"\n--- {miniModel} ---");
Console.WriteLine($"перевод (ka): {miniKa.Text}");
Console.WriteLine($"обратно (ru): {await BackToRussianAsync(miniKa.Text)}");

// 2) Старшая модель переводит тот же текст. 
var strongKa = await strong.GetResponseAsync(translatePrompt, new ChatOptions { Temperature = 0f });

Console.WriteLine($"\n--- {strongModel} ---");
Console.WriteLine($"перевод (ka): {strongKa.Text}");
Console.WriteLine($"обратно (ru): {await BackToRussianAsync(strongKa.Text)}");

return;

async Task<string> BackToRussianAsync(string georgian)
{
    var back = await strong.GetResponseAsync(
        "Translate this Georgian text into Russian literally, preserving any errors or nonsense. " +
        $"Output ONLY the translation.\n\n{georgian}",
        new ChatOptions { Temperature = 0f });
    return back.Text;
}

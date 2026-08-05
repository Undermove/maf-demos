using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var apiKey = config["OpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException(
        "Не задан OpenAI:ApiKey. Скопируй appsettings.local.json.example → appsettings.local.json и впиши ключ.");

var baseUrl = config["OpenAI:BaseUrl"];
var miniModel = config["OpenAI:MiniModel"] ?? "gpt-4o-mini";
var strongModel = config["OpenAI:StrongModel"] ?? "gpt-4o";

OpenAIClient NewOpenAI() => string.IsNullOrWhiteSpace(baseUrl)
    ? new OpenAIClient(new ApiKeyCredential(apiKey))
    : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

// Логируем реальную модель через MAF-middleware (.AsBuilder().Use(...)) — тот же приём, что на слайде,
// только на слое IChatClient, где в ответе доступен ChatResponse.ModelId.
IChatClient WithModelLog(IChatClient inner) => inner
    .AsBuilder()
    .Use(
        async (IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken ct) =>
        {
            var response = await innerClient.GetResponseAsync(messages, options, ct);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"✔ реально ответила модель: {response.ModelId}");
            Console.ResetColor();
            return response;
        },
        null)
    .Build();

IChatClient miniClient = WithModelLog(NewOpenAI().GetChatClient(miniModel).AsIChatClient());
IChatClient strongClient = WithModelLog(NewOpenAI().GetChatClient(strongModel).AsIChatClient());

const string russian = "Добро пожаловать в Додо!";
const string targetCode = "ka";        // грузинский
const string targetName = "Georgian";

// Суб-агент-эксперт на СТАРШЕЙ модели, экспонированный как ИНСТРУМЕНТ (agent-as-tool).
AIAgent expertTranslator = strongClient.AsAIAgent(
    instructions: "You are an expert translator for low-resource languages. " +
                  "Translate accurately and idiomatically, never transliterate. Output only the translation.",
    name: "expert_translator",
    description: "Переводит текст на малые (low-resource) языки — грузинский, армянский, азербайджанский и т.п. — с высоким качеством.");

AITool expertTool = expertTranslator.AsAIFunction();

//  ВАРИАНТ 1 — только малая модель, переводит сама (на грузинском выходит слабо)
AIAgent agent = miniClient.AsAIAgent(
    instructions: "You are a translator. Translate the user's text into the requested target language. " +
                  "Output only the translation.",
    name: "translator");

//  ВАРИАНТ 2 — малая модель-ДИСПЕТЧЕР сама делегирует эксперту на малых языках.
// AIAgent agent = miniClient.AsAIAgent(
//     instructions: "You translate the user's text into the requested target language. " +
//                   "IMPORTANT: if the target language is low-resource (ka, hy, az, kk, ky, uz, tg), " +
//                   "DO NOT translate it yourself — call the `expert_translator` tool and return exactly its result. " +
//                   "For well-supported languages, translate yourself.",
//     name: "dispatcher",
//     description: "Dispatcher",
//     tools: new List<AITool> { expertTool });

Console.WriteLine($"Малая модель   : {miniModel}");
Console.WriteLine($"Старшая модель : {strongModel}");
Console.WriteLine();
Console.WriteLine($"Текст (ru): {russian}");
Console.WriteLine($"Переводим на: {targetName} ({targetCode})");
Console.WriteLine();

var response = await agent.RunAsync($"Translate into {targetName} ({targetCode}): {russian}");

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"\nПеревод: {response.Text}");
Console.ResetColor();

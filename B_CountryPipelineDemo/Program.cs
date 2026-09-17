using System.ClientModel;
using D_CountryPipelineDemo;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#region config

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var apiKey = config["OpenAI:ApiKey"]
             ?? throw new InvalidOperationException("Не задан OpenAI:ApiKey в appsettings.local.json.");
var githubToken = config["GitHub:Token"]
                  ?? throw new InvalidOperationException("Не задан GitHub:Token в appsettings.local.json.");

var owner = config["GitHub:Owner"] ?? "Undermove";
var repo = config["GitHub:Repo"] ?? "maf-country-opening-demo";
var baseUrl = config["OpenAI:BaseUrl"];
var model = config["OpenAI:MiniModel"] ?? "gpt-4o-mini";

var openAi = string.IsNullOrWhiteSpace(baseUrl)
    ? new OpenAIClient(new ApiKeyCredential(apiKey))
    : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

var github = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("maf-country-pipeline-demo"))
{
    Credentials = new Octokit.Credentials(githubToken),
};

var messenger = new MessengerTools(config["Telegram:BotToken"], config["Telegram:ChatId"]);

// Режим сброса демо в исходное состояние: dotnet run -- reset
// «БД» с шагами пайплайна: ямлики + флаг done, как в проде.
var db = new StepsDb(Path.Combine(AppContext.BaseDirectory, "pipeline.db"));
db.EnsureSeeded();

// Разовая подготовка доски: одна карточка на каждый шаг. dotnet run -- seed
if (args.Contains("seed"))
{
    await DemoSeed.RunAsync(github, owner, repo, db);
    return;
}

// Режим сброса демо в исходное состояние: dotnet run -- reset
if (args.Contains("reset"))
{
    await DemoReset.RunAsync(github, owner, repo, githubToken, db);
    return;
}

#endregion

var board = new BoardTools(github, owner, repo, githubToken);
var git = new GitHubTools(github, owner, repo);
var telegram = new TelegramTools(messenger);

IChatClient chatClient = openAi.GetChatClient(model).AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation(configure: client => client.FunctionInvoker = LogToolCall)
    .Build();

var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "CountryPipelineAgent",
        UseProvidedChatClientAsIs = true,
        ChatOptions = new ChatOptions
        {
            Instructions =
                "Ты — агент процесса открытия стран в Dodo. Тебе по одному дают шаги пайплайна. " +
                "У шага есть ямлик — в нём только граф: id шага и от чего он зависит. Что именно делать, " +
                "в ямлике НЕ написано: инструкции лежат в карточке на доске. " +
                "Поэтому всегда начинай с того, что читаешь свою карточку, и делаешь ровно то, " +
                "что в ней написано. Если для работы нужны данные, которых нет ни в карточке, ни в её комментариях, " +
                "спроси человека — не выдумывай. " +
                "Выполняй ТОЛЬКО текущий шаг, кратко отчитывайся по-русски.",
            Tools =
            [
                AIFunctionFactory.Create(board.ListCards),
                AIFunctionFactory.Create(board.ReadCard),
                AIFunctionFactory.Create(board.AddCardComment),
                AIFunctionFactory.Create(board.MoveCard),
                AIFunctionFactory.Create(telegram.AskManager),
                AIFunctionFactory.Create(git.ReadFile),
                AIFunctionFactory.Create(git.CreateBranch),
                AIFunctionFactory.Create(git.CommitFile),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(git.CreatePullRequest)),
            ],
        },
    });

// Интерактивный интерфейс поверх ТОГО ЖЕ агента: dotnet run -- serve
if (args.Contains("serve"))
{
    var builder = WebApplication.CreateBuilder();
    builder.Services.AddAGUIServer();                 // ← раз

    var app = builder.Build();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapAGUIServer("/agui", agent);                // ← два

    Console.WriteLine("▶ Интерфейс: http://localhost:5199\n");
    await app.RunAsync("http://localhost:5199");
    return;
}

// Одна сессия на весь пайплайн: контекст (например, языки из шага 1) доступен в следующих шагах.
var session = await agent.CreateSessionAsync();

Console.WriteLine($"▶ Репозиторий: https://github.com/{owner}/{repo}");
Console.WriteLine($"▶ Канал связи: {(messenger.TelegramEnabled ? "Telegram" : "консоль (Telegram не настроен)")}\n");

// ДЕТЕРМИНИРОВАННЫЙ каркас: цикл и порядок шагов — код. Свобода агента — только внутри шага.
while (db.GetNextStep() is { } step)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"══════ Шаг {step.Id}/{db.TotalSteps()}: {step.CardTitle} ══════");
    Console.ResetColor();
    Console.WriteLine(step.Yaml);
    Console.WriteLine($"  → инструкции: карточка #{step.Card} на доске\n");

    var response = await agent.RunAsync(
        $"Выполни шаг пайплайна. Граф шага:\n{step.Yaml}\n\n" +
        $"Инструкции к этому шагу — в карточке #{step.Card} на доске. Прочитай её и сделай то, что в ней написано.",
        session);

    // Human-in-the-loop: агент попросил подтверждение → шлём человеку, ответ возвращаем агенту.
    while (response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList() is { Count: > 0 } approvals)
    {
        var replies = new List<AIContent>();
        foreach (var request in approvals)
        {
            var call = request.ToolCall as FunctionCallContent;
            var callArgs = call?.Arguments is null ? "" : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));
            var approved = await messenger.ApproveAsync($"{call?.Name}({callArgs})");
            replies.Add(request.CreateResponse(approved));
        }

        response = await agent.RunAsync(new ChatMessage(ChatRole.User, replies), session);
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n■ Отчёт агента: {response.Text}\n");
    Console.ResetColor();

    db.MarkDone(step.Id);
}

Console.WriteLine("✅ Пайплайн завершён: все шаги выполнены.");

// Мидлвара вызова функций: видит имя и аргументы каждого тула, логирует и передаёт выполнение дальше.
static async ValueTask<object?> LogToolCall(FunctionInvocationContext context, CancellationToken cancellationToken)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"   🔧 {context.Function.Name} ({string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"))})");
    Console.ResetColor();

    return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
}

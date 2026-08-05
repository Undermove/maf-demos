using System.ClientModel;
using D_CountryPipelineDemo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
if (args.Contains("reset"))
{
    await DemoReset.RunAsync(github, owner, repo, githubToken);
    return;
}

// «БД» с шагами пайплайна: ямлики + флаг done, как в проде.
var db = new StepsDb(Path.Combine(AppContext.BaseDirectory, "pipeline.db"));
db.EnsureSeeded();

#endregion


// Три отдельных набора тулов: доска, git и связь с человеком.
var board = new BoardTools(github, owner, repo, githubToken);
var git = new GitHubTools(github, owner, repo);
var telegram = new TelegramTools(messenger);

// МИДЛВАРА на уровне функций: каждый вызов тула проходит через FunctionInvoker.
// Логирование живёт в одном месте — сами тулы про консоль ничего не знают.
IChatClient chatClient = openAi.GetChatClient(model).AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation(configure: client => client.FunctionInvoker = LogToolCall)
    .Build();

// Открытие PR — только с подтверждением человека (в Telegram или консоли).
var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "CountryPipelineAgent",
        UseProvidedChatClientAsIs = true, // конвейер клиента собрали сами — агенту не надо заворачивать его повторно
        ChatOptions = new()
        {
            Instructions =
                "Ты — агент процесса открытия стран в Dodo. Тебе по одному дают шаги пайплайна в виде YAML-карточек " +
                "с чек-листами. Выполняй пункты чек-листа текущей карточки по порядку (учитывай depends_on): " +
                "type: ai — делай сам тулами; type: manual — выясняй у человека тулом AskManager. " +
                "Выполняй ТОЛЬКО текущую карточку, не выдумывай данные, кратко отчитывайся по-русски.",
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

// Одна сессия на весь пайплайн: контекст (например, языки из шага 1) доступен в следующих шагах.
var session = await agent.CreateSessionAsync();

Console.WriteLine($"▶ Репозиторий: https://github.com/{owner}/{repo}");
Console.WriteLine($"▶ Канал связи: {(messenger.TelegramEnabled ? "Telegram" : "консоль (Telegram не настроен)")}\n");

// ДЕТЕРМИНИРОВАННЫЙ каркас: цикл и порядок шагов — код. Свобода агента — только внутри шага.
while (db.GetNextStep() is { } step)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"══════ Шаг {step.Id}/{db.TotalSteps()} из БД ══════");
    Console.ResetColor();
    Console.WriteLine(step.Yaml);

    var response = await agent.RunAsync($"Выполни шаг пайплайна:\n{step.Yaml}", session);

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

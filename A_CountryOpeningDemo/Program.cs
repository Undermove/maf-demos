using System.ClientModel;
using CountryOpeningDemo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

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
                  ?? throw new InvalidOperationException("Не задан GitHub:Token (PAT со scope `repo`) в appsettings.local.json.");

var owner = config["GitHub:Owner"] ?? "Undermove";
var repo = config["GitHub:Repo"] ?? "maf-country-opening-demo";
var baseUrl = config["OpenAI:BaseUrl"];
var model = config["OpenAI:MiniModel"] ?? "gpt-4o-mini";

var openAi = string.IsNullOrWhiteSpace(baseUrl)
    ? new OpenAIClient(new ApiKeyCredential(apiKey))
    : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

var github = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("maf-country-opening-demo"))
{
    Credentials = new Octokit.Credentials(githubToken),
};

#endregion

// Создаем чат-клиента, который вызывает АПИ
ChatClient chatClient = openAi.GetChatClient(model);

// Создаем инструменты и превращаем в список AITool
var tools = new GitHubTools(github, owner, repo);
IList<AITool> aiTools = [AIFunctionFactory.Create(tools.ReadFile)];

// Соединяем все в агента = модель + инструменты
ChatClientAgent agent = chatClient.AsAIAgent(
    instructions: "Ты помощник по репозиторию Dodo. Используй инструменты, отвечай по-русски и коротко.",
    name: "RepoAgent",
    tools: aiTools);

const string question = "Прочитай countries/germany.json и расскажи, о чём этот файл.";

Console.WriteLine($"▶ Репозиторий: https://github.com/{owner}/{repo}");
Console.WriteLine($"▶ Вопрос: {question}\n");

var response = await agent.RunAsync(question);

Console.WriteLine($"\n■ {response.Text}");

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Octokit;

namespace D_CountryPipelineDemo;

/// <summary>
/// Тулы для доски задач: карточки — issues репозитория, добавленные в GitHub Project (Projects v2).
/// MoveCard двигает карточку по колонкам доски (Status-филд через GraphQL) и дублирует статус лейблом.
/// </summary>
public sealed class BoardTools(GitHubClient client, string owner, string repo, string token)
{
    // Колонки демо → опции Status-филда доски.
    private static readonly Dictionary<string, string> ColumnToStatus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backlog"] = "Todo",
        ["in-progress"] = "In Progress",
        ["done"] = "Done",
    };

    private readonly HttpClient _http = CreateHttp(token);

    [Description("List open task cards on the board: number, title and status label of each")]
    public async Task<string> ListCards()
    {
        var issues = await client.Issue.GetAllForRepository(owner, repo);
        var cards = issues
            .Where(i => i.PullRequest is null)
            .Select(i => $"#{i.Number}: {i.Title} [{string.Join(", ", i.Labels.Select(l => l.Name))}]");
        return string.Join("\n", cards);
    }

    [Description("Read a task card from the board")]
    public async Task<string> ReadCard(
        [Description("Card (issue) number")] int number)
    {
        var issue = await client.Issue.Get(owner, repo, number);
        return $"Card #{number}: {issue.Title}\n\n{issue.Body}";
    }

    [Description("Add a comment to a task card")]
    public async Task<string> AddCardComment(
        [Description("Card (issue) number")] int number,
        [Description("Comment text")] string text)
    {
        await client.Issue.Comment.Create(owner, repo, number, text);
        return $"Comment added to card #{number}.";
    }

    [Description("Move a task card to another board column: backlog, in-progress or done")]
    public async Task<string> MoveCard(
        [Description("Card (issue) number")] int number,
        [Description("Target column: backlog, in-progress or done")] string column)
    {
        column = column.ToLowerInvariant().Trim();

        // 1) Двигаем карточку на доске (Projects v2, Status-филд) — GraphQL.
        var (projectId, itemId, fieldId, optionId) = await ResolveBoardIdsAsync(number, ColumnToStatus[column]);
        await GraphQlAsync($$"""
            mutation {
              updateProjectV2ItemFieldValue(input: {
                projectId: "{{projectId}}", itemId: "{{itemId}}",
                fieldId: "{{fieldId}}", value: { singleSelectOptionId: "{{optionId}}" }
              }) { projectV2Item { id } }
            }
            """);

        // 2) Дублируем статус лейблом на issue — видно и вне доски.
        var issue = await client.Issue.Get(owner, repo, number);
        var update = issue.ToUpdate();
        foreach (var label in issue.Labels.Where(l => l.Name.StartsWith("status:")))
            update.RemoveLabel(label.Name);
        update.AddLabel($"status:{column}");
        await client.Issue.Update(owner, repo, number, update);

        return $"Card #{number} moved to '{column}'.";
    }

    /// <summary>Находит карточку issue на доске и id нужной опции Status — один запрос, без хардкода id.</summary>
    private async Task<(string ProjectId, string ItemId, string FieldId, string OptionId)> ResolveBoardIdsAsync(
        int issueNumber, string statusName)
    {
        var data = await GraphQlAsync($$"""
            query {
              repository(owner: "{{owner}}", name: "{{repo}}") {
                issue(number: {{issueNumber}}) {
                  projectItems(first: 5) {
                    nodes {
                      id
                      project {
                        id
                        field(name: "Status") {
                          ... on ProjectV2SingleSelectField { id options { id name } }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        var item = data.GetProperty("repository").GetProperty("issue")
            .GetProperty("projectItems").GetProperty("nodes")[0];
        var field = item.GetProperty("project").GetProperty("field");
        var optionId = field.GetProperty("options").EnumerateArray()
            .First(o => o.GetProperty("name").GetString() == statusName)
            .GetProperty("id").GetString()!;

        return (item.GetProperty("project").GetProperty("id").GetString()!,
                item.GetProperty("id").GetString()!,
                field.GetProperty("id").GetString()!,
                optionId);
    }

    private async Task<JsonElement> GraphQlAsync(string query)
    {
        var payload = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("https://api.github.com/graphql", payload);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (json.TryGetProperty("errors", out var errors))
            throw new InvalidOperationException($"GraphQL error: {errors}");
        return json.GetProperty("data");
    }

    private static HttpClient CreateHttp(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        http.DefaultRequestHeaders.Add("User-Agent", "maf-country-pipeline-demo");
        return http;
    }
}

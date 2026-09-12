using Microsoft.Data.Sqlite;

namespace D_CountryPipelineDemo;

/// <summary>
/// «База данных» пайплайна — SQLite-файл рядом с демкой. В проде это Postgres, схема та же.
///
/// РАЗДЕЛЕНИЕ ОТВЕТСТВЕННОСТИ (то, ради чего эта демка и существует):
///   • ямлик в базе  — ТОЛЬКО граф: id шага, от кого зависит, что даёт на выход;
///   • карточка на доске — ТОЛЬКО контекст: что именно надо сделать, человеческими словами.
/// Один шаг = один ямлик = одна карточка. Инструкции не дублируются.
/// </summary>
public sealed partial class StepsDb(string path)
{
    private readonly string _connectionString = $"Data Source={path}";

    /// <summary>Шаг пайплайна: граф в ямлике, инструкции — в карточке на доске.</summary>
    public sealed record Step(long Id, string Yaml, int Card, string CardTitle);

    // Формат ямликов — как в проде, но без единой строчки инструкций: их место на доске.
    private static readonly (string Title, string Yaml)[] Seed =
    [
        (
            "Выяснить требования для открытия страны",
            """
            version: "1.0"

            step:
              id: clarify_requirements
              card: ?            # карточка на доске — там написано, ЧТО делать
              depends_on: []
              provides: [country_code, currency, languages]
            """
        ),
        (
            "Добавить конфиг страны",
            """
            version: "1.0"

            step:
              id: add_country_config
              card: ?            # карточка на доске — там написано, ЧТО делать
              depends_on: [clarify_requirements]
              requires: [country_code, currency, languages]
              provides: [branch]
            """
        ),
        (
            "Открыть pull request с конфигом страны",
            """
            version: "1.0"

            step:
              id: open_pull_request
              card: ?            # карточка на доске — там написано, ЧТО делать
              depends_on: [add_country_config]
              requires: [branch]
              provides: [pull_request_url]
            """
        ),
    ];

    /// <summary>Тексты карточек. Отсюда их создаёт `dotnet run -- seed` — в самой демке они уже на доске.</summary>
    public static readonly (string Title, string Body)[] Cards =
    [
        (
            "Выяснить требования для открытия страны",
            """
            Открываем новую страну: **Грузия**, код страны `ge`, валюта `GEL`.

            Что нужно сделать:

            - Спросить у менеджера страны, какие языки нужны для локализации.
            - Привести ответ к двухбуквенным ISO-кодам (например, «грузинский и английский» → `ka`, `en`).
            - Записать коды языков комментарием в эту карточку.
            - Передвинуть карточку в `in-progress`.
            """
        ),
        (
            "Добавить конфиг страны",
            """
            Репозиторий: https://github.com/Undermove/maf-country-opening-demo
            Образец конфига: `countries/germany.json`

            Что нужно сделать:

            - Прочитать образец `countries/germany.json`.
            - Создать ветку `feature/country-<код страны>`.
            - Добавить `countries/<код страны>.json` строго по структуре образца.
              Код страны и валюту взять из карточки «Выяснить требования для открытия страны»,
              языки — из комментария к ней, поле `status` — `opening`.
            - Передвинуть карточку в `in-progress`.
            """
        ),
        (
            "Открыть pull request с конфигом страны",
            """
            Что нужно сделать:

            - Открыть pull request из ветки с конфигом страны в `main`.
            - Добавить ссылку на pull request комментарием в эту карточку.
            - Передвинуть карточку в `done`.
            """
        ),
    ];

    public void EnsureSeeded()
    {
        using var db = Open();
        Exec(db, """
                 CREATE TABLE IF NOT EXISTS steps (
                     id    INTEGER PRIMARY KEY,
                     title TEXT    NOT NULL,
                     yaml  TEXT    NOT NULL,
                     card  INTEGER NOT NULL DEFAULT 0,
                     done  INTEGER NOT NULL DEFAULT 0)
                 """);

        if ((long)Scalar(db, "SELECT COUNT(*) FROM steps")! == 0)
            foreach (var (title, yaml) in Seed)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO steps (title, yaml) VALUES ($title, $yaml)";
                cmd.Parameters.AddWithValue("$title", title);
                cmd.Parameters.AddWithValue("$yaml", yaml);
                cmd.ExecuteNonQuery();
            }

        // Все шаги выполнены прошлым прогоном — сбрасываем, демо можно гонять по кругу.
        if ((long)Scalar(db, "SELECT COUNT(*) FROM steps WHERE done = 0")! == 0)
        {
            Exec(db, "UPDATE steps SET done = 0");
            Console.WriteLine("♻ Все шаги были выполнены — сбрасываю пайплайн для нового прогона.\n");
        }
    }

    /// <summary>
    /// Привязать шаг к карточке на доске (делает `dotnet run -- seed`).
    /// Номер проставляется и в колонку, и в сам ямлик: связь «шаг ↔ карточка» должна быть видна
    /// там же, где виден граф, а не только в схеме базы.
    /// </summary>
    public void BindCard(string title, int card)
    {
        using var db = Open();

        var yaml = (string?)Scalar(db, $"SELECT yaml FROM steps WHERE title = '{title.Replace("'", "''")}'");
        if (yaml is null) return;

        yaml = CardLine().Replace(yaml, $"  card: {card}");

        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE steps SET card = $card, yaml = $yaml WHERE title = $title";
        cmd.Parameters.AddWithValue("$card", card);
        cmd.Parameters.AddWithValue("$yaml", yaml);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.ExecuteNonQuery();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^  card: .*$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex CardLine();

    public IReadOnlyList<int> CardNumbers()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT card FROM steps WHERE card > 0 ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var result = new List<int>();
        while (reader.Read()) result.Add(reader.GetInt32(0));
        return result;
    }

    /// <summary>Человекочитаемое состояние пайплайна — для интерфейса на AG-UI.</summary>
    [System.ComponentModel.Description("Шаги пайплайна открытия страны: порядок, карточка на доске и выполнен ли шаг")]
    public string Describe()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, title, card, done FROM steps ORDER BY id";
        using var reader = cmd.ExecuteReader();

        var lines = new List<string>();
        while (reader.Read())
            lines.Add($"Шаг {reader.GetInt64(0)}: {reader.GetString(1)} — карточка #{reader.GetInt32(2)}, " +
                      (reader.GetInt32(3) == 1 ? "выполнен" : "не выполнен"));

        return lines.Count == 0 ? "Шагов нет." : string.Join("\n", lines);
    }

    public Step? GetNextStep()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, yaml, card, title FROM steps WHERE done = 0 ORDER BY id LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Step(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    /// <summary>Сбросить прогресс, не трогая привязку шагов к карточкам.</summary>
    public void ResetProgress()
    {
        using var db = Open();
        Exec(db, "UPDATE steps SET done = 0");
    }

    public void MarkDone(long id)
    {
        using var db = Open();
        Exec(db, $"UPDATE steps SET done = 1 WHERE id = {id}");
    }

    public long TotalSteps()
    {
        using var db = Open();
        return (long)Scalar(db, "SELECT COUNT(*) FROM steps")!;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}

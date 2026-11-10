using DocRouter.Core;

namespace DocRouter.Engine;

/// <summary>Проверка настроек из окружения на старте: все папки, группы адресатов и подключение к PostgreSQL должны быть заданы.</summary>
public static class ConfigCheck
{
    public static IReadOnlyList<string> MissingSettings(DirectoriesOptions dirs, MailOptions mail, PostgresOptions pg)
    {
        var errors = new List<string>();
        Check(dirs, "Dirs", errors);
        Check(mail, "Mail", errors);
        Check(pg, "Postgres", errors);
        return errors;
    }

    private static void Check(object options, string section, List<string> errors)
    {
        foreach (var p in options.GetType().GetProperties().Where(p => p.PropertyType == typeof(string)))
            if (string.IsNullOrWhiteSpace((string?)p.GetValue(options)))
                errors.Add($"Не задана настройка {section}__{p.Name}");
    }
}

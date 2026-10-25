using System.Text;

namespace LLMNetHelper.Chat;

/// <summary>
/// Провайдер файла транскрипта диалога (план 22, этап 4.7): уникальный путь
/// и UTF-8-писатель. Вынесен из <see cref="ChatDialogLogger"/>, который
/// занимается только Markdown-содержимым сеанса.
/// </summary>
public static class DialogFileProvider
{
    /// <summary>
    /// Создаёт каталог при необходимости, открывает UTF-8-писатель (без BOM,
    /// AutoFlush) для нового файла транскрипта и возвращает полный путь файла.
    /// </summary>
    public static StreamWriter CreateWriter(string directory, out string filePath)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);

        filePath = UniqueFilePath(dir, DateTimeOffset.UtcNow);
        return new StreamWriter(filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <summary>
    /// Имя файла <c>chat-yyyy-MM-dd_HH-mm-ssZ.md</c> (UTC старта сеанса);
    /// при коллизии — суффиксы <c>-2</c>, <c>-3</c>, …
    /// </summary>
    public static string UniqueFilePath(string directory, DateTimeOffset utcNow)
    {
        // Литералы 'chat' и 'Z' ОБЯЗАТЕЛЬНО в одинарных кавычках: без них
        // "h" (час) и "t" (AM/PM) в спецификаторах формата исказят имя
        // ("chat-..." -> "c6P-...").
        var stem = utcNow.ToString("'chat'-yyyy-MM-dd_HH-mm-ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, stem + ".md");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem}-{suffix}.md");
            suffix++;
        }

        return candidate;
    }
}
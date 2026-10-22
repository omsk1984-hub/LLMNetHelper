using System.Diagnostics;
using System.Text;

namespace ArtasTools.MonitorAgent.CodeChange;

/// <summary>
/// Результат запуска внешнего процесса.
/// </summary>
/// <param name="ExitCode">Код возврата.</param>
/// <param name="StdOut">Стандартный вывод.</param>
/// <param name="StdErr">Ошибки.</param>
/// <param name="TimedOut">Был ли превышен таймаут.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    /// <summary>Успешно ли завершился процесс.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>Краткое описание для отчёта.</summary>
    public string Describe()
    {
        if (TimedOut)
        {
            return "превышен таймаут";
        }

        if (Succeeded)
        {
            return "ok";
        }

        var tail = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
        tail = tail.Length > 1500 ? tail[^1500..] : tail;
        return $"exit={ExitCode}: {tail.Trim()}";
    }
}

/// <summary>
/// Запуск внешних процессов (git, dotnet) с таймаутом и отменой.
/// <para>
/// Важно: при таймауте завершается <b>только собственное дочернее дерево</b>
/// процессов (<c>Kill(entireProcessTree: true)</c>). Никаких глобальных
/// <c>Stop-Process</c>/<c>taskkill</c> по чужим процессам — это прямо запрещено
/// правилами проекта.
/// </para>
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Запускает процесс и ждёт завершения.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, "", $"не удалось запустить {fileName}", false);
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"сбой запуска {fileName}: {ex.Message}", false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKillTree(process);
        }

        // Даже после убийства дочитываем буферы, иначе повиснем на незакрытых pipe.
        var stdout = await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeAwaitAsync(stderrTask).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    /// <summary>
    /// Завершает только своё дочернее дерево.
    /// </summary>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился.
        }
        catch (Exception)
        {
            // Нет прав или процесс уже мёртв — не считаем это фатальным.
        }
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }
}

using System.Text.RegularExpressions;

namespace Deneb.Core;

public enum DownloadPhase { Queued, GlobalPause, ManualPause, Completed, Failed, NeedsDecision, Probing, ProbeRetry, Transferring, Retrying, Ready, Stopped, Waiting, Assembling, Verifying }
public enum ProblemCode { EmptyInput, InvalidUrl, InvalidSettings, AbsolutePath, StoreLocked, UnsupportedStore, CorruptStore, MissingParts, Persistence, IdentityUnknown, NoResume, Disk, NetworkExhausted, BadRange, UnsafeResume, RangeChanged, TagChanged, UnexpectedResponse, FileChanged, ModifiedChanged, Disconnected, TooMuchData, IncompleteResponse, SegmentSize, FinalSize, FinalMetadata, Timeout, Network, MissingFile, SystemCommand, SystemLaunch, SingleName, IntegerSettings, HttpUnavailable, HttpDenied, HttpRetry, Unexpected, Legacy }
public sealed record Problem(ProblemCode Code, int? Status = null, string? LegacyText = null)
{
    public static Problem FromException(Exception ex) => ex switch
    {
        ProblemException known => known.Problem,
        IOException or UnauthorizedAccessException => new(ProblemCode.Disk),
        HttpRequestException => new(ProblemCode.Network),
        OperationCanceledException => new(ProblemCode.Timeout),
        _ => new(ProblemCode.Unexpected)
    };
}
public class ProblemException(Problem problem) : Exception(problem.Code.ToString())
{
    public Problem Problem { get; } = problem;
    public ProblemException(ProblemCode code) : this(new Problem(code)) { }
}

// Compatibility only: old rendered diagnostics are never used in engine decisions.
public static class LegacyProblems
{
    public static Problem? Convert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var known = text switch
        {
            "Добавьте хотя бы одну HTTP/HTTPS-ссылку." => (ProblemCode?)ProblemCode.EmptyInput,
            "Нужна корректная HTTP/HTTPS-ссылка без логина и пароля в адресе." => (ProblemCode?)ProblemCode.InvalidUrl,
            "Укажите абсолютную папку и от 1 до 16 файлов/соединений." => (ProblemCode?)ProblemCode.InvalidSettings,
            "Папка должна быть абсолютным путём." => (ProblemCode?)ProblemCode.AbsolutePath,
            "Это хранилище Deneb уже открыто другим процессом." => (ProblemCode?)ProblemCode.StoreLocked,
            "Неподдерживаемая версия хранилища Deneb." => (ProblemCode?)ProblemCode.UnsupportedStore,
            "Основное и резервное хранилище повреждены. Файлы загрузок сохранены." => (ProblemCode?)ProblemCode.CorruptStore,
            "Часть сохранённых данных отсутствует. Начните заново." => (ProblemCode?)ProblemCode.MissingParts,
            "Не удалось сохранить состояние: проверьте место и права доступа." => (ProblemCode?)ProblemCode.Persistence,
            "Нельзя подтвердить прежний файл. Замените ссылку или начните заново." => (ProblemCode?)ProblemCode.IdentityUnknown,
            "Сервер не поддерживает докачку. Можно начать заново." => (ProblemCode?)ProblemCode.NoResume,
            "Ошибка диска: проверьте свободное место и права доступа." => (ProblemCode?)ProblemCode.Disk,
            "Сетевая ошибка после повторных попыток. Можно продолжить позже." => (ProblemCode?)ProblemCode.NetworkExhausted,
            "Сервер вернул неверный Content-Range." => (ProblemCode?)ProblemCode.BadRange,
            "Без валидатора и поддержки Range безопасная докачка невозможна. Начните заново." => (ProblemCode?)ProblemCode.UnsafeResume,
            "Сервер изменил файл или диапазон. Данные не были дописаны; требуется решение." => (ProblemCode?)ProblemCode.RangeChanged,
            "ETag файла изменился. Требуется начать заново." => (ProblemCode?)ProblemCode.TagChanged,
            "Неожиданный ответ сервера." => (ProblemCode?)ProblemCode.UnexpectedResponse,
            "Файл изменился между запросами. Начните заново." => (ProblemCode?)ProblemCode.FileChanged,
            "Дата изменения файла изменилась. Начните заново." => (ProblemCode?)ProblemCode.ModifiedChanged,
            "Соединение оборвалось." => (ProblemCode?)ProblemCode.Disconnected,
            "Сервер прислал больше данных, чем заявлено." => (ProblemCode?)ProblemCode.TooMuchData,
            "Неполный ответ сервера." => (ProblemCode?)ProblemCode.IncompleteResponse,
            "Размер сегмента не совпадает с сохранённым состоянием." => (ProblemCode?)ProblemCode.SegmentSize,
            "Итоговый размер файла не совпадает." => (ProblemCode?)ProblemCode.FinalSize,
            "Не удалось сохранить метаданные готового файла." => (ProblemCode?)ProblemCode.FinalMetadata,
            "Истекло время ожидания ответа." => (ProblemCode?)ProblemCode.Timeout,
            "Сетевая ошибка соединения." => (ProblemCode?)ProblemCode.Network,
            "Файл перемещён или удалён." => (ProblemCode?)ProblemCode.MissingFile,
            "Системная команда не выполнена." => (ProblemCode?)ProblemCode.SystemCommand,
            "Не удалось запустить системную команду macOS." => (ProblemCode?)ProblemCode.SystemLaunch,
            "Своё имя можно указать только для одной ссылки." => (ProblemCode?)ProblemCode.SingleName,
            "Введите целые числа от 1 до 16." => (ProblemCode?)ProblemCode.IntegerSettings,
            "Непредвиденная ошибка. Операция не выполнена." => (ProblemCode?)ProblemCode.Unexpected,
            _ => null
        };
        if (known.HasValue) return new(known.Value);
        var http = Regex.Match(text, @"^HTTP (\d{3})(.*)$");
        if (http.Success)
        {
            var status = int.Parse(http.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var tail = http.Groups[2].Value;
            if (tail == "") return new(ProblemCode.HttpRetry, status);
            if (tail == ": доступ отклонён. Проверьте или замените ссылку.") return new(ProblemCode.HttpDenied, status);
            if (tail is ": файл недоступен." or ": загрузка недоступна.") return new(ProblemCode.HttpUnavailable, status);
        }
        if (text.StartsWith("Ошибка загрузки: ", StringComparison.Ordinal)) return new(ProblemCode.Unexpected);
        return new(ProblemCode.Legacy, LegacyText: text);
    }
}

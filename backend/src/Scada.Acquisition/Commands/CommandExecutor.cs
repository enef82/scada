using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Acquisition.Devices;
using Scada.Acquisition.Engine;
using Scada.Core.Data;
using Scada.Core.Model;
using Scada.Core.Processing;

namespace Scada.Acquisition.Commands;

/// <summary>
/// Исполнитель команд управления. Правила безопасности:
///  * команда захватывается атомарно (pending → executing), исполняется не более одного раза;
///  * истёкшая команда (expires_at ≤ now) не исполняется никогда — в т.ч. после восстановления связи;
///  * автоматических повторов записи нет;
///  * после записи — контрольное чтение, результат фиксируется в таблице commands.
/// </summary>
public sealed class CommandExecutor
{
    private readonly NpgsqlDataSource _ds;
    private readonly DeviceManager _devices;
    private readonly TagEngine _engine;
    private readonly ILogger<CommandExecutor> _log;
    private readonly AcquisitionOptions _opt;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public CommandExecutor(NpgsqlDataSource ds, DeviceManager devices, TagEngine engine,
        IOptions<AcquisitionOptions> opt, ILogger<CommandExecutor> log)
    {
        _ds = ds;
        _devices = devices;
        _engine = engine;
        _log = log;
        _opt = opt.Value;
    }

    /// <summary>При старте службы: незавершённые команды прошлого запуска не повторяются.</summary>
    public async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE commands SET status = CASE WHEN status = 'executing' THEN 'failed' ELSE 'expired' END,
                   result = CASE WHEN status = 'executing'
                                 THEN 'прервана перезапуском службы сбора, результат неизвестен'
                                 ELSE 'истёк срок до запуска службы сбора' END,
                   finished_at = now()
            WHERE status = 'executing' OR (status = 'pending' AND expires_at <= now())
            """);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) _log.LogWarning("Закрыто незавершённых команд прошлого запуска: {Count}", n);
    }

    /// <summary>Обработка всех pending-команд (после переподключения LISTEN или по уведомлению).</summary>
    public async Task ScanPendingAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            var ids = new List<long>();
            await using (var cmd = _ds.CreateCommand("SELECT id FROM commands WHERE status = 'pending' ORDER BY id"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
            foreach (var id in ids) await ExecuteAsync(id, ct);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task ExecuteAsync(long id, CancellationToken ct)
    {
        // атомарный захват; истёкшие сразу помечаются expired
        long tagId;
        double? num;
        string? str;
        string user;
        await using (var claim = _ds.CreateCommand(
            """
            UPDATE commands SET status = CASE WHEN expires_at > now() THEN 'executing' ELSE 'expired' END,
                   result = CASE WHEN expires_at > now() THEN NULL ELSE 'истёк срок жизни команды' END,
                   finished_at = CASE WHEN expires_at > now() THEN NULL ELSE now() END
            WHERE id = $1 AND status = 'pending'
            RETURNING status, tag_id, value_num, value_str, requested_by
            """))
        {
            claim.Parameters.Add(Db.P(id));
            await using var r = await claim.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;             // уже захвачена или не существует
            if (r.GetString(0) != "executing")
            {
                _log.LogWarning("Команда {Id} не исполнена: истёк срок жизни", id);
                return;
            }
            tagId = r.GetInt32(1);
            num = r.GetNullableDouble(2);
            str = r.GetNullableString(3);
            user = r.GetString(4);
        }

        string status, result;
        double? rbNum = null;
        string? rbStr = null;
        try
        {
            (rbNum, rbStr, result) = await DoWriteAsync((int)tagId, num, str, ct);
            status = "done";
            _log.LogInformation("Команда {Id} от {User}: тег {Tag} ← {Value} — выполнено",
                id, user, tagId, num?.ToString(CultureInfo.InvariantCulture) ?? str);
        }
        catch (CommandRejectedException ex)
        {
            status = "rejected";
            result = ex.Message;
            _log.LogWarning("Команда {Id} отклонена: {Reason}", id, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "failed";
            result = "служба сбора остановлена во время исполнения";
        }
        catch (Exception ex)
        {
            status = "failed";
            result = ex is OperationCanceledException ? "тайм-аут исполнения" : ex.Message;
            _log.LogWarning("Команда {Id} не выполнена: {Error}", id, result);
        }

        await using var done = _ds.CreateCommand(
            "UPDATE commands SET status=$2, result=$3, readback_num=$4, readback_str=$5, finished_at=now() WHERE id=$1");
        done.Parameters.Add(Db.P(id));
        done.Parameters.Add(Db.P(status));
        done.Parameters.Add(Db.P(result));
        done.Parameters.Add(Db.P(rbNum));
        done.Parameters.Add(Db.P(rbStr));
        await done.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<(double?, string?, string)> DoWriteAsync(int tagId, double? num, string? str, CancellationToken ct)
    {
        if (!_engine.TryGetTag(tagId, out var tag)) throw new CommandRejectedException("тег не найден в конфигурации службы");
        if (!tag.Enabled) throw new CommandRejectedException("тег отключён");
        if (!tag.Writable) throw new CommandRejectedException("тег только для чтения");

        var worker = _devices.Get(tag.DeviceId) ?? throw new CommandRejectedException("устройство не опрашивается");
        if (!worker.IsOnline) throw new CommandRejectedException("нет связи с устройством");

        object raw;
        if (tag.DataType == TagDataType.String)
        {
            raw = str ?? throw new CommandRejectedException("не задано строковое значение");
        }
        else
        {
            var v = num ?? throw new CommandRejectedException("не задано числовое значение");
            if (tag.WriteMin is { } min && v < min) throw new CommandRejectedException($"значение ниже допустимого ({min})");
            if (tag.WriteMax is { } max && v > max) throw new CommandRejectedException($"значение выше допустимого ({max})");
            raw = tag.DataType == TagDataType.Bool ? (v != 0 ? 1.0 : 0.0) : ValueConverter.Unscale(tag, v);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.CommandExecuteTimeoutMs);
        var readback = await worker.WriteAsync(tag, raw, timeout.Token);

        if (readback is not { } rb) return (null, null, "записано, контрольное чтение не выполнено");
        var norm = ValueConverter.Normalize(tag, rb, DateTime.UtcNow);
        if (!norm.Quality.IsGood()) return (norm.Num, norm.Str, $"записано, качество контрольного чтения: {norm.Quality}");

        var match = tag.DataType == TagDataType.String
            ? norm.Str == str
            : norm.Num is { } n && Math.Abs(n - num!.Value) <= Math.Max(1e-6, Math.Abs(num.Value) * 1e-4);
        return (norm.Num, norm.Str, match ? "записано, подтверждено чтением" : "записано, прочитано другое значение");
    }
}

public sealed class CommandRejectedException(string message) : Exception(message);

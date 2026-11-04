using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scada.Core.Config;

public sealed record FieldChange(string Field, JsonNode? Old, JsonNode? New);

/// <summary>Изменение одного объекта между двумя снимками.</summary>
public sealed record ChangeItem(
    string ObjectType,          // device | tag | alarm | mnemo
    int Id,
    string Name,
    string Op,                  // created | updated | deleted
    IReadOnlyList<FieldChange> Fields,
    bool Highlight);            // адрес, право записи, подтверждение, удаление — показываются отдельно

/// <summary>Сравнение снимков: что добавлено, изменено и удалено, с полями «было / стало».</summary>
public static class ConfigDiff
{
    // поля, изменение которых особенно важно для безопасности
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "endpoint", "protocol", "writable", "writeMin", "writeMax", "writeConfirm", "deviceId",
        "rawLow", "rawHigh", "engLow", "engHigh", "enabled", "areaId", "commandClass",
    };

    public static List<ChangeItem> Compute(ConfigSnapshot prev, ConfigSnapshot next)
    {
        var list = new List<ChangeItem>();
        Compare(list, "device", prev.Devices, next.Devices, d => d.Id, d => d.Name);
        var tagNames = next.Tags.Concat(prev.Tags).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Name);
        Compare(list, "tag", prev.Tags, next.Tags, t => t.Id, t => t.Name);
        Compare(list, "alarm", prev.Alarms, next.Alarms, a => a.Id,
            a => $"{tagNames.GetValueOrDefault(a.TagId, "?")}: {a.Message}");
        Compare(list, "mnemo", prev.Mnemos, next.Mnemos, m => m.Id, m => m.Name);
        return list;
    }

    private static void Compare<T>(List<ChangeItem> list, string type, IEnumerable<T> prev, IEnumerable<T> next,
        Func<T, int> id, Func<T, string> name)
    {
        var a = prev.ToDictionary(id);
        var b = next.ToDictionary(id);
        foreach (var (key, item) in b.OrderBy(x => name(x.Value), StringComparer.CurrentCulture))
        {
            if (!a.TryGetValue(key, out var old))
            {
                list.Add(new ChangeItem(type, key, name(item), "created", [], type is "tag" or "device"));
                continue;
            }
            var fields = FieldDiff(old!, item!);
            if (fields.Count > 0)
                list.Add(new ChangeItem(type, key, name(item), "updated", fields,
                    fields.Any(f => Sensitive.Contains(f.Field))));
        }
        foreach (var (key, item) in a.Where(x => !b.ContainsKey(x.Key)).OrderBy(x => name(x.Value), StringComparer.CurrentCulture))
            list.Add(new ChangeItem(type, key, name(item), "deleted", [], true));
    }

    private static List<FieldChange> FieldDiff<T>(T old, T next)
    {
        var o = JsonSerializer.SerializeToNode(old, ConfigSnapshot.Json)!.AsObject();
        var n = JsonSerializer.SerializeToNode(next, ConfigSnapshot.Json)!.AsObject();
        var changes = new List<FieldChange>();
        foreach (var (field, nv) in n)
        {
            if (field == "id") continue;
            var ov = o[field];
            if (!JsonNode.DeepEquals(ov, nv))
                changes.Add(new FieldChange(field, ov?.DeepClone(), nv?.DeepClone()));
        }
        return changes;
    }

    /// <summary>Короткая сводка: «добавлено 12 тегов, изменено 3, удалена 1 тревога».</summary>
    public static Dictionary<string, Dictionary<string, int>> Summary(IEnumerable<ChangeItem> changes) =>
        changes.GroupBy(c => c.ObjectType)
            .ToDictionary(g => g.Key, g => g.GroupBy(c => c.Op).ToDictionary(x => x.Key, x => x.Count()));

    /// <summary>Области, которые затрагивает изменение: прежняя и новая (перенос объекта между областями).</summary>
    public static int[] ChangeAreas(ChangeItem c, ConfigSnapshot prev, ConfigSnapshot next)
    {
        IEnumerable<int> In(ConfigSnapshot s) => c.ObjectType switch
        {
            "device" => s.Devices.Where(d => d.Id == c.Id).Select(d => d.AreaId),
            "tag" => s.Tags.Where(t => t.Id == c.Id).Select(s.TagArea),
            "alarm" => s.Alarms.Where(a => a.Id == c.Id).SelectMany(a => s.Tags.Where(t => t.Id == a.TagId)).Select(s.TagArea),
            "mnemo" => s.Mnemos.Where(m => m.Id == c.Id).Select(m => m.AreaId),
            _ => [],
        };
        // перенос устройства меняет область всех его тегов без собственной области
        var extra = c.ObjectType == "device"
            ? prev.Tags.Concat(next.Tags).Where(t => t.DeviceId == c.Id && t.AreaId is null).SelectMany(t => new[] { prev.TagArea(t), next.TagArea(t) })
            : [];
        return In(prev).Concat(In(next)).Concat(extra).Select(a => a > 0 ? a : 1).Distinct().ToArray();
    }
}

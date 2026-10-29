using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed class ReportTemplateStore
{
    public static ReportTemplateStore Default { get; } = new();
    private readonly JsonFileStore<ReportTemplateDto> _store = new("report-templates.json");

    public List<ReportTemplateDto> ReadAll() => _store.ReadAll();
    public ReportTemplateDto? Find(string id) => _store.ReadAll().FirstOrDefault(item => item.Id == id);

    public ReportTemplateDto Require(string id)
    {
        var item = Find(id) ?? throw new KeyNotFoundException("所选报告模板不存在或已删除。");
        ReportTemplateRenderer.Validate(item.Content);
        return item;
    }

    public ReportTemplateDto Create(ReportTemplateDto input)
    {
        Validate(input);
        var item = input with { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        _store.Update(rows => { rows.Add(item); return 0; });
        return item;
    }

    public ReportTemplateDto? Update(string id, ReportTemplateDto input)
    {
        Validate(input);
        return _store.Update<ReportTemplateDto?>(rows =>
        {
            var index = rows.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            return rows[index] = input with { Id = id, CreatedAt = rows[index].CreatedAt };
        });
    }

    public bool Delete(string id) => _store.Update(rows => rows.RemoveAll(item => item.Id == id) > 0);

    private static void Validate(ReportTemplateDto input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200)
            throw new ArgumentException("模板名称须为 1–200 个字符。");
        ReportTemplateRenderer.Validate(input.Content);
    }
}

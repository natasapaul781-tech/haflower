namespace CleanC.Core;

/// <summary>
/// 清理执行引擎：对传入的清理项逐项执行“分析”或“清理”，并通过回调实时上报进度与日志。
/// 本身不接触 UI 控件，由调用方负责跨线程回贴。
/// </summary>
public sealed class CleanerEngine
{
    private readonly Action<string>? _log;

    public CleanerEngine(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>逐项估算占用大小，返回 item.Id -> 字节数。</summary>
    public async Task<Dictionary<string, long>> AnalyzeAsync(
        IEnumerable<CleanItem> items, Action<int, int>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, long>();
        var arr = items.ToArray();

        for (int i = 0; i < arr.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i, arr.Length);
            var it = arr[i];
            _log?.Invoke($"正在分析：{it.Name} ...");
            long size = 0;
            try
            {
                size = await it.Estimate(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                size = 0;
                _log?.Invoke($"  [{it.Name}] 分析失败：{ex.Message}");
            }
            result[it.Id] = size;
            _log?.Invoke($"  {it.Name} = {SizeFormatter.Format(size)}");
        }

        progress?.Invoke(arr.Length, arr.Length);
        return result;
    }

    /// <summary>逐项清理选中项，返回总体释放字节与错误汇总。</summary>
    public async Task<CleanSummary> CleanAsync(
        IEnumerable<CleanItem> items, Action<int, int>? progress, CancellationToken ct)
    {
        var summary = new CleanSummary();
        var arr = items.ToArray();
        summary.ItemsCount = arr.Length;

        for (int i = 0; i < arr.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i, arr.Length);
            var it = arr[i];
            _log?.Invoke($"清理：{it.Name} ...");

            try
            {
                var r = await it.Clean(ct);
                summary.FreedBytes += r.FreedBytes;
                summary.Errors.AddRange(r.Errors);
                _log?.Invoke($"  [完成] {it.Name} 释放 {SizeFormatter.Format(r.FreedBytes)}");
                foreach (var e in r.Errors)
                    _log?.Invoke($"    ~ {e}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                summary.Errors.Add($"{it.Name}: {ex.Message}");
                _log?.Invoke($"  [失败] {it.Name}: {ex.Message}");
            }
        }

        progress?.Invoke(arr.Length, arr.Length);
        return summary;
    }
}

namespace CleanC.Core;

/// <summary>
/// 分区操作所用的 PowerShell 脚本构造器。
///
/// 每个破坏性脚本都以**复验**开头：重新读取分区真实状态并比对 GUID / 起始偏移 / 大小，
/// 任一不符立即 `throw` 中止——这样即使计划是在几分钟前生成的、期间分区被别的程序改过，
/// 也不会在错误的对象上执行操作。
///
/// 抽成独立类是为了让这些守卫可以被单元断言（见 tools/UiSmokeTest）。
/// </summary>
public static class PartitionScripts
{
    private const string Prelude =
        PowerShellRunner.Prelude + "$ErrorActionPreference='Stop'\n";

    private const string PartitionSelect =
        "Select-Object DiskNumber,PartitionNumber," +
        "@{n='DriveLetter';e={ if($_.DriveLetter){[string]$_.DriveLetter}else{''} }}," +
        "@{n='Offset';e={[uint64]$_.Offset}}," +
        "@{n='Size';e={[uint64]$_.Size}}," +
        "@{n='TypeName';e={[string]$_.Type}}," +
        "@{n='GptType';e={[string]$_.GptType}}," +
        "@{n='Guid';e={[string]$_.Guid}}," +
        "@{n='IsBoot';e={[bool]$_.IsBoot}}," +
        "@{n='IsSystem';e={[bool]$_.IsSystem}}," +
        "@{n='IsHidden';e={[bool]$_.IsHidden}}," +
        "@{n='IsActive';e={[bool]$_.IsActive}}," +
        "@{n='IsShadowCopy';e={[bool]$_.IsShadowCopy}}," +
        "@{n='NoDefaultDriveLetter';e={[bool]$_.NoDefaultDriveLetter}}," +
        "@{n='MbrType';e={[int]$_.MbrType}}";

    /// <summary>收缩分区（MSFT_Partition.Resize）。</summary>
    public static string Shrink(PartitionIdentity id, long targetBytes) =>
        Prelude + $$"""
            try {
              $p = Get-Partition -DiskNumber {{id.DiskNumber}} -PartitionNumber {{id.PartitionNumber}} -ErrorAction Stop
              if (-not $p) { throw '未找到该分区（可能已被删除或编号已变化）' }
              if ('{{Escape(id.Guid)}}' -ne '' -and [string]$p.Guid -ne '{{Escape(id.Guid)}}') { throw '分区身份不符（GUID 变化），已中止' }
              if ([uint64]$p.Offset -ne [uint64]{{id.OffsetBytes}}) { throw '分区起始偏移已变化，已中止' }
              if ([uint64]$p.Size -ne [uint64]{{id.SizeBytes}}) { throw '分区大小已变化，已中止' }
              $before = [uint64]$p.Size
              $r = Invoke-CimMethod -InputObject $p -MethodName Resize -Arguments @{ Size = [uint64]{{targetBytes}} } -ErrorAction Stop
              $p2 = Get-Partition -DiskNumber {{id.DiskNumber}} -PartitionNumber {{id.PartitionNumber}} -ErrorAction Stop
              $after = if ($p2) { [uint64]$p2.Size } else { [uint64]0 }
              [pscustomobject]@{ Ok=([int]$r.ReturnValue -eq 0); ReturnValue=[int]$r.ReturnValue;
                Extended=[string]$r.ExtendedStatus; Before=$before; After=$after } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; Extended=$_.Exception.Message;
                Before=[uint64]0; After=[uint64]0 } | ConvertTo-Json -Compress
            }
            """;

    /// <summary>
    /// 在未分配空间新建分区并分配盘符（不指定起始位置，由 Windows 自行选择空闲区域）。
    /// 分盘流程用它——那一步释放出的空间就在源分区右侧，位置唯一。
    /// </summary>
    public static string CreatePartition(int diskNumber, long sizeBytes, char letter) =>
        CreatePartition(diskNumber, null, sizeBytes, letter);

    /// <summary>
    /// 在未分配空间新建分区并分配盘符。
    ///
    /// ⚠ 尺寸与偏移必须写成 <c>([uint64]N)</c> 或纯数字：**参数模式下 <c>-Size [uint64]N</c> 不会求值**，
    /// PowerShell 会把它当成字符串 <c>"[uint64]N"</c> 传给 cmdlet，绑定时才报
    /// 「无法将值"[uint64]157300760064"转换为类型 System.UInt64」（真事故：Q: 已收缩、
    /// 新建 D: 一步整段失败，磁盘上留下 146.5 GB 未分配空间）。
    /// 哈希表值（<c>@{ Size = [uint64]N }</c>）与括号内的表达式属于表达式上下文，没有这个问题。
    /// </summary>
    /// <param name="offsetBytes">
    /// 起始偏移；**必须已按 1 MB 对齐**（未对齐时系统会拒绝）。给定时会在创建后核对实际偏移，
    /// 避免"以为建在这里、其实建在别处"。这里会再做一次兜底对齐——脚本是真正和系统打交道的那一层，
    /// 调用方算错也不该变成"建不出来"或"建在别处"。
    /// </param>
    /// <param name="sizeBytes">
    /// 大小。**必须向下取整到 1 MB**：Windows 建分区会按对齐补足尺寸，给一个"除不尽 1 MB"的值，
    /// 它会要一个更大的整数 MB，于是直接报 <c>Not enough available capacity</c>。
    /// 这里同样做兜底取整。
    /// </param>
    public static string CreatePartition(int diskNumber, long? offsetBytes, long sizeBytes, char letter)
    {
        long alignedSize = (long)DiskLayoutSnapshot.AlignDown((ulong)Math.Max(0, sizeBytes));
        if (alignedSize <= 0) alignedSize = sizeBytes;   // 极端小值：保持原样，让系统去报错

        long? alignedOffset = offsetBytes is { } raw
            ? (long)DiskLayoutSnapshot.AlignUp((ulong)Math.Max(0, raw))
            : null;

        string offsetClause = alignedOffset is { } off ? $" -Offset ([uint64]{off})" : string.Empty;
        string offsetCheck = alignedOffset is { } off2
            ? $"\n  if ([uint64]$np.Offset -ne [uint64]{off2}) {{ throw '新建分区的起始位置与计划不符，请检查磁盘布局' }}"
            : string.Empty;

        return Prelude + $$"""
            try {
              $np = New-Partition -DiskNumber {{diskNumber}}{{offsetClause}} -Size ([uint64]{{alignedSize}}) -DriveLetter {{letter}} -ErrorAction Stop
              if (-not $np) { throw 'New-Partition 未返回新分区对象' }{{offsetCheck}}
              $sel = $np | {{PartitionSelect}}
              [pscustomobject]@{ Ok=$true; ReturnValue=0; Extended=''; Partition=$sel } | ConvertTo-Json -Depth 4 -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; Extended=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;
    }

    /// <summary>
    /// 格式化新建分区。三重身份校验（GUID + 偏移 + 大小）+ 必须带盘符：
    /// **绝不按盘符定位格式化目标**，这是整个功能里风险最高的一步。
    /// </summary>
    public static string FormatNewPartition(PartitionInfo created, string guid, string label) =>
        Prelude + $$"""
            try {
              $p = Get-Partition -DiskNumber {{created.DiskNumber}} -PartitionNumber {{created.PartitionNumber}} -ErrorAction Stop
              if (-not $p) { throw '未找到刚创建的分区，已中止（拒绝格式化未知分区）' }
              if ('{{Escape(guid)}}' -ne '' -and [string]$p.Guid -ne '{{Escape(guid)}}') { throw '分区身份不符（GUID），拒绝格式化' }
              if ([uint64]$p.Offset -ne [uint64]{{created.OffsetBytes}}) { throw '分区身份不符（偏移），拒绝格式化' }
              if ([uint64]$p.Size -ne [uint64]{{created.SizeBytes}}) { throw '分区身份不符（大小），拒绝格式化' }
              if (-not $p.DriveLetter) { throw '该分区没有盘符，为安全起见拒绝格式化' }
              Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel '{{PartitionPlanBuilder.SanitizeLabel(label)}}' -Confirm:$false -ErrorAction Stop | Out-Null
              [pscustomobject]@{ Ok=$true; ReturnValue=0; Extended='' } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; Extended=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

    /// <summary>删除分区（脚本内先复验身份与尺寸）。</summary>
    public static string RemovePartition(PartitionIdentity id) =>
        Prelude + $$"""
            try {
              $p = Get-Partition -DiskNumber {{id.DiskNumber}} -PartitionNumber {{id.PartitionNumber}} -ErrorAction Stop
              if (-not $p) { throw '未找到该分区（可能已被删除）' }
              if ('{{Escape(id.Guid)}}' -ne '' -and [string]$p.Guid -ne '{{Escape(id.Guid)}}') { throw '源分区身份不符（GUID），已中止' }
              if ([uint64]$p.Offset -ne [uint64]{{id.OffsetBytes}}) { throw '源分区起始偏移已变化，已中止' }
              if ([uint64]$p.Size -ne [uint64]{{id.SizeBytes}}) { throw '源分区大小已变化，已中止' }
              Remove-Partition -DiskNumber {{id.DiskNumber}} -PartitionNumber {{id.PartitionNumber}} -Confirm:$false -ErrorAction Stop
              [pscustomobject]@{ Ok=$true; ReturnValue=0; Extended='' } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; Extended=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

    /// <summary>
    /// 把目标分区紧邻右侧的连续空闲空间**全部**并入。
    /// 关键：先用 GetSupportedSize 实测 SizeMax，确认空闲量不少于 <paramref name="expectedFreeBytes"/>
    /// （源分区容量）才执行 Resize —— 避免出现“删了分区却扩不了容”的不可逆损失。
    /// </summary>
    public static string ExtendAdjacent(PartitionIdentity target, long expectedFreeBytes) =>
        Prelude + $$"""
            try {
              $t = Get-Partition -DiskNumber {{target.DiskNumber}} -PartitionNumber {{target.PartitionNumber}} -ErrorAction Stop
              if (-not $t) { throw '未找到目标分区，已中止' }
              if ('{{Escape(target.Guid)}}' -ne '' -and [string]$t.Guid -ne '{{Escape(target.Guid)}}') { throw '目标分区身份不符（GUID），已中止' }
              if ([uint64]$t.Offset -ne [uint64]{{target.OffsetBytes}}) { throw '目标分区起始偏移已变化，已中止' }
              $before = [uint64]$t.Size
              $s = Invoke-CimMethod -InputObject $t -MethodName GetSupportedSize -ErrorAction Stop
              if ([int]$s.ReturnValue -ne 0) { throw "无法实测可扩展范围，返回码 $($s.ReturnValue)：$($s.ExtendedStatus)" }
              $free = [uint64]$s.SizeMax - $before
              $expected = [uint64]{{expectedFreeBytes}}
              if ($free -lt $expected) {
                throw "紧邻右侧的连续空闲空间只有 $free 字节，少于预期的 $expected 字节；为避免出现「删了分区却扩不了容」，已中止扩容。"
              }
              $target = $before + $free
              $r = Invoke-CimMethod -InputObject $t -MethodName Resize -Arguments @{ Size = $target } -ErrorAction Stop
              $t2 = Get-Partition -DiskNumber {{target.DiskNumber}} -PartitionNumber {{target.PartitionNumber}} -ErrorAction Stop
              $after = if ($t2) { [uint64]$t2.Size } else { [uint64]0 }
              [pscustomobject]@{ Ok=([int]$r.ReturnValue -eq 0); ReturnValue=[int]$r.ReturnValue;
                Extended=[string]$r.ExtendedStatus; Before=$before; After=$after; Free=$free } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; Extended=$_.Exception.Message;
                Before=[uint64]0; After=[uint64]0; Free=[uint64]0 } | ConvertTo-Json -Compress
            }
            """;

    /// <summary>保存一个未分配空间的可用量查询（供 UI 在扩容前提示）。</summary>
    public static string SupportedSize(int diskNumber, int partitionNumber) =>
        Prelude + $$"""
            try {
              $p = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} -ErrorAction Stop
              if (-not $p) { throw '未找到该分区' }
              $r = Invoke-CimMethod -InputObject $p -MethodName GetSupportedSize -ErrorAction Stop
              [pscustomobject]@{ Ok=$true; ReturnValue=[int]$r.ReturnValue; SizeMin=[uint64]$r.SizeMin;
                SizeMax=[uint64]$r.SizeMax; Extended=[string]$r.ExtendedStatus } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; SizeMin=[uint64]0; SizeMax=[uint64]0;
                Extended=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

    /// <summary>单引号转义：PowerShell 单引号字符串内用两个单引号表示一个单引号。</summary>
    private static string Escape(string? value) => (value ?? string.Empty).Replace("'", "''");
}

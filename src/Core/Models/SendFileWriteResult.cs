namespace Core.Models;

// 文件型 Send 写入(创建或替换)的结果:服务端分配的 SendId/AccessId,以及本次是否替换了既有 Send。
public sealed record SendFileWriteResult(
    string SendId,
    string AccessId,
    bool ReplacedExisting);

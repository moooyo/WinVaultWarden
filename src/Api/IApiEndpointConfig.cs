namespace Api;

// 端点配置 seam:设置服务端基址。与能力(读/写)契约分离,
// 避免配置关切泄漏进每个角色接口。
public interface IApiEndpointConfig
{
    void SetBaseAddress(string baseUrl);
}

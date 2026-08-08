using System.Net;
using System.Net.Http.Json;
using Acm.Api.Dtos;
using Acm.Api.Tests.TestEnvironment;
using Xunit;

namespace Acm.Api.Tests.M1;

// 接入统一 collection：WorkerFixture 构造注入默认连接串（本类不调 StartAsync，纯 CRUD 无需 Worker）
[Collection(WorkerFixture.Collection)]
public class AuthProblemTests(TestAppFactory factory, WorkerFixture worker)
    : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly TestAppFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => _factory.CleanDbAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_Login_And_Full_Crud()
    {
        // 1. 注册
        var r = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "me", password = "pw1234" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var token = (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // 2. 无 token 访问应 401
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/v1/problems")).StatusCode);

        // 3. 登录用户可创建题目（个人自用，无 admin 分级）
        r = await _client.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "P1001", title = "A+B Problem", description = "读两数输出和",
            inputDesc = "两个整数", outputDesc = "和", tags = new[] { "入门" },
            difficulty = 1, sampleInputs = "1 2", sampleOutputs = "3",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var pid = (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Id;

        // 4. 列表关键字搜索
        var list = await _client.GetFromJsonAsync<ProblemListResponse>(
            "/api/v1/problems?keyword=A%2BB");
        Assert.Equal(1, list!.Total);

        // 5. tag 筛选
        list = await _client.GetFromJsonAsync<ProblemListResponse>("/api/v1/problems?tag=入门");
        Assert.Equal(1, list!.Total);

        // 6. update
        r = await _client.PutAsJsonAsync($"/api/v1/problems/{pid}", new { title = "A+B Test" });
        Assert.Equal("A+B Test", (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Title);

        // 7. delete
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/v1/problems/{pid}")).StatusCode);

        // 8. 删除后列表应为空
        list = await _client.GetFromJsonAsync<ProblemListResponse>("/api/v1/problems");
        Assert.Equal(0, list!.Total);
    }
}

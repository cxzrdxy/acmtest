using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Acm.Api.Dtos;
using Acm.Api.Tests.TestEnvironment;
using Acm.Judge.Core.Judge;
using Xunit;

namespace Acm.Api.Tests.M2;

/// <summary>评测集成测试：连真实 Postgres + Redis + Worker 进程 + Docker 沙箱，验证异步评测全链路。</summary>
[Collection(WorkerFixture.Collection)]
public class SubmitJudgeTests(
    TestAppFactory factory, WorkerFixture worker)
    : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly TestAppFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    // 本次测试创建的题目 ID（测试点目录按它建/删）
    private int _pid;

    public async Task InitializeAsync()
    {
        await worker.StartAsync();   // 幂等：compose db/redis + Worker 子进程就绪
        await _factory.CleanDbAsync();

        // 注册 + 登录
        var r = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "judge", password = "pw1234" });
        var token = (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // 造题
        r = await _client.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "P2001", title = "A+B Judge", description = "sum",
            sampleInputs = "3 5", sampleOutputs = "8",
        });
        _pid = (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Id;

        // 造测试点文件（2 个：3 5→8 / 10 20→30）
        var root = _factory.Services.GetRequiredService<IOptions<JudgeOptions>>().Value.TestcaseRoot;
        var tcDir = Path.Combine(root, _pid.ToString());
        Directory.CreateDirectory(tcDir);
        File.WriteAllText(Path.Combine(tcDir, "1.in"), "3 5");
        File.WriteAllText(Path.Combine(tcDir, "1.out"), "8");
        File.WriteAllText(Path.Combine(tcDir, "2.in"), "10 20");
        File.WriteAllText(Path.Combine(tcDir, "2.out"), "30");
    }

    public async Task DisposeAsync()
    {
        // 清理测试点文件目录
        var root = _factory.Services.GetRequiredService<IOptions<JudgeOptions>>().Value.TestcaseRoot;
        var tcDir = Path.Combine(root, _pid.ToString());
        if (Directory.Exists(tcDir)) Directory.Delete(tcDir, true);
    }

    // 提交（断言秒回 PENDING），然后轮询等待终态（Worker 后台评测写库）
    private static async Task<SubmissionRead> SubmitAndWaitAsync(HttpClient client, int pid, object body)
    {
        var r = await client.PostAsJsonAsync($"/api/v1/problems/{pid}/submissions", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var sub = await r.Content.ReadFromJsonAsync<SubmissionRead>();
        Assert.Equal("PENDING", sub!.Status);
        Assert.True(DateTime.UtcNow - sub.CreatedAt < TimeSpan.FromSeconds(2), "提交应立即返回");

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            var cur = await client.GetFromJsonAsync<SubmissionRead>($"/api/v1/submissions/{sub.Id}");
            if (cur!.Status is "AC" or "WA" or "TLE" or "CE" or "RE") return cur;
        }
        throw new TimeoutException($"提交 {sub.Id} 轮询 60 次未到终态（Worker 是否在跑？）");
    }

    [Fact]
    public async Task Submit_Ac_Cpp_Returns_Ac_Score100_Detail()
    {
        var sub = await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b);}"
        });

        Assert.Equal("AC", sub.Status);
        Assert.Equal(100, sub.Score);
        Assert.Equal(2, sub.Detail.Count);
        Assert.All(sub.Detail, d => Assert.Equal("AC", d.Status));
        Assert.True(sub.TimeMs > 0, "timeMs 应大于 0");
        Assert.Null(sub.CompileError);
    }

    [Fact]
    public async Task Submit_Wa_Cpp_Returns_Wa()
    {
        var sub = await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b+1);}"
        });

        Assert.Equal("WA", sub.Status);
        Assert.Equal(0, sub.Score);
        Assert.Equal(2, sub.Detail.Count);
        Assert.All(sub.Detail, d => Assert.Equal("WA", d.Status));
    }

    [Fact]
    public async Task Submit_CompileError_Cpp_Returns_CE_With_Message()
    {
        var sub = await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "int main() {\n    return\n}"
        });

        Assert.Equal("CE", sub.Status);
        Assert.Equal(0, sub.Score);
        Assert.Empty(sub.Detail);
        Assert.False(string.IsNullOrEmpty(sub.CompileError), "compileError 应包含编译错误文本");
    }

    [Fact]
    public async Task Submit_Ac_Python_Returns_Ac()
    {
        var sub = await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "python3",
            code = "a,b=map(int,input().split())\nprint(a+b)"
        });

        Assert.Equal("AC", sub.Status);
        Assert.Equal(100, sub.Score);
    }

    [Fact]
    public async Task Submit_Invalid_Language_Returns_400()
    {
        var r = await _client.PostAsJsonAsync($"/api/v1/problems/{_pid}/submissions", new
        {
            language = "java",
            code = "x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Submit_InfiniteLoop_Cpp_Returns_Tle()
    {
        var sub = await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "int main(){while(1){}}"
        });

        Assert.Equal("TLE", sub.Status);
        Assert.Equal(0, sub.Score);
        Assert.All(sub.Detail, d => Assert.Equal("TLE", d.Status));
    }

    [Fact]
    public async Task Submit_Updates_Counters_FirstAc_Only()
    {
        // 第一次 AC → AcCount=1, SubmitCount=1
        await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b);}"
        });

        // 第二次 AC（同用户）→ AcCount 仍 1, SubmitCount=2
        await SubmitAndWaitAsync(_client, _pid, new
        {
            language = "cpp17",
            code = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b);}"
        });

        var problem = await _client.GetFromJsonAsync<ProblemRead>($"/api/v1/problems/{_pid}");
        Assert.Equal(2, problem!.SubmitCount);
        Assert.Equal(1, problem.AcCount);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Acm.Api.Dtos;
using Acm.Judge.Core.Judge;
using Xunit;

namespace Acm.Api.Tests.M3;

/// <summary>提交历史集成测试：列表（仅自己/最新在前/筛选/分页/题目名）+ 详情含代码。</summary>
public class SubmissionHistoryTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly TestAppFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private int _pid1;   // P3001 HistOne
    private int _pid2;   // P3002 HistTwo
    private int[] _pids = null!;

    public async Task InitializeAsync()
    {
        await _factory.CleanDbAsync();

        // 建题用户（仅用于 InitializeAsync 造题；各测试独立注册新用户，数据互不污染）
        var token = await RegisterAsync(_client, "histowner");
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        _pid1 = await CreateProblemAsync("P3001", "HistOne");
        _pid2 = await CreateProblemAsync("P3002", "HistTwo");
        _pids = new[] { _pid1, _pid2 };

        // 两题共用 a+b 测试点（先删残留目录，防止旧 pid 同名数据）
        foreach (var pid in _pids)
        {
            var tcDir = TestcaseDir(pid);
            if (Directory.Exists(tcDir)) Directory.Delete(tcDir, true);
            Directory.CreateDirectory(tcDir);
            File.WriteAllText(Path.Combine(tcDir, "1.in"), "3 5");
            File.WriteAllText(Path.Combine(tcDir, "1.out"), "8");
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var pid in _pids)
        {
            var tcDir = TestcaseDir(pid);
            if (Directory.Exists(tcDir)) Directory.Delete(tcDir, true);
        }
    }

    private string TestcaseDir(int pid) => Path.Combine(
        _factory.Services.GetRequiredService<IOptions<JudgeOptions>>().Value.TestcaseRoot,
        pid.ToString());

    // 注册独立用户 → 新 HttpClient + Bearer
    private async Task<(HttpClient client, string token)> NewUserAsync(string name)
    {
        var client = _factory.CreateClient();
        var token = await RegisterAsync(client, name);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (client, token);
    }

    // 注册（响应即含 AccessToken）
    private static async Task<string> RegisterAsync(HttpClient client, string username)
    {
        var r = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "pw1234" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private async Task<int> CreateProblemAsync(string slug, string title)
    {
        var r = await _client.PostAsJsonAsync("/api/v1/problems", new
        {
            slug, title, description = "sum",
            sampleInputs = "3 5", sampleOutputs = "8",
        });
        return (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Id;
    }

    // 提交（秒回 PENDING）→ 轮询终态（Worker 后台评测）
    private static async Task<SubmissionRead> SubmitAndWaitAsync(HttpClient client, int pid, object body)
    {
        var r = await client.PostAsJsonAsync($"/api/v1/problems/{pid}/submissions", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var sub = await r.Content.ReadFromJsonAsync<SubmissionRead>();
        Assert.Equal("PENDING", sub!.Status);

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            var cur = await client.GetFromJsonAsync<SubmissionRead>($"/api/v1/submissions/{sub.Id}");
            if (cur!.Status is "AC" or "WA" or "TLE" or "CE" or "RE") return cur;
        }
        throw new TimeoutException($"提交 {sub.Id} 轮询 60 次未到终态（Worker 是否在跑？）");
    }

    private static Task<SubmissionListResponse> ListAsync(HttpClient client, int? problemId = null,
        string? status = null, int page = 1, int size = 20)
    {
        var url = $"/api/v1/submissions?page={page}&size={size}";
        if (problemId.HasValue) url += $"&problemId={problemId}";
        if (!string.IsNullOrEmpty(status)) url += $"&status={status}";
        return client.GetFromJsonAsync<SubmissionListResponse>(url)!;
    }

    private const string AcCpp = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b);}";
    private const string WaCpp = "#include <cstdio>\nint main(){int a,b;scanf(\"%d%d\",&a,&b);printf(\"%d\\n\",a+b+1);}";

    [Fact]
    public async Task List_Only_Shows_Own_Submissions()
    {
        var (clientA, _) = await NewUserAsync("histA");
        var (clientB, _) = await NewUserAsync("histB");

        var subA = await SubmitAndWaitAsync(clientA, _pid1, new { language = "cpp17", code = AcCpp });
        var subB = await SubmitAndWaitAsync(clientB, _pid1, new { language = "cpp17", code = AcCpp });

        var listA = await ListAsync(clientA);
        Assert.Equal(1, listA!.Total);
        Assert.Equal(subA.Id, listA.Items[0].Id);

        var listB = await ListAsync(clientB);
        Assert.Equal(1, listB!.Total);
        Assert.Equal(subB.Id, listB.Items[0].Id);
    }

    [Fact]
    public async Task List_Newest_First()
    {
        var (client, _) = await NewUserAsync("histNewest");

        var ids = new List<long>();
        for (int i = 0; i < 3; i++)
            ids.Add((await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code = AcCpp })).Id);

        var list = await ListAsync(client);
        Assert.Equal(3, list!.Total);
        Assert.Equal(ids[2], list.Items[0].Id);   // 最新（第 3 交）在前
        Assert.Equal(ids[1], list.Items[1].Id);
        Assert.Equal(ids[0], list.Items[2].Id);
        Assert.True(list.Items[0].Id > list.Items[1].Id && list.Items[1].Id > list.Items[2].Id);
    }

    [Fact]
    public async Task List_Filter_By_Problem()
    {
        var (client, _) = await NewUserAsync("histP");
        await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code = AcCpp });
        await SubmitAndWaitAsync(client, _pid2, new { language = "cpp17", code = AcCpp });

        var list = await ListAsync(client, problemId: _pid1);
        Assert.Equal(1, list!.Total);
        Assert.Equal(_pid1, list.Items[0].ProblemId);
    }

    [Fact]
    public async Task List_Filter_By_Status()
    {
        var (client, _) = await NewUserAsync("histS");
        await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code = AcCpp });
        await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code = WaCpp });

        var list = await ListAsync(client, status: "WA");
        Assert.Equal(1, list!.Total);
        Assert.Equal("WA", list.Items[0].Status);
    }

    [Fact]
    public async Task List_Paging_Newest_First()
    {
        var (client, _) = await NewUserAsync("histPaging");
        var ids = new List<long>();
        for (int i = 0; i < 3; i++)
            ids.Add((await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code = AcCpp })).Id);

        // size=1 第 2 页 → 第 2 新提交
        var page2 = await ListAsync(client, page: 2, size: 1);
        Assert.Equal(3, page2!.Total);
        Assert.Single(page2.Items);
        Assert.Equal(ids[1], page2.Items[0].Id);
    }

    [Fact]
    public async Task List_Item_Has_ProblemTitle()
    {
        var (client, _) = await NewUserAsync("histTitle");
        await SubmitAndWaitAsync(client, _pid2, new { language = "cpp17", code = AcCpp });

        var list = await ListAsync(client);
        Assert.Equal("HistTwo", list!.Items[0].ProblemTitle);
    }

    [Fact]
    public async Task Get_Detail_Contains_Code()
    {
        var (client, _) = await NewUserAsync("histCode");
        var code = "#include <cstdio>\nint main(){printf(\"hello\\n\");}";
        var sub = await SubmitAndWaitAsync(client, _pid1, new { language = "cpp17", code });

        Assert.Equal(code, sub.Code);
    }
}

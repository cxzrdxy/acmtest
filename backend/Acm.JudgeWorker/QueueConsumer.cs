using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Judge;

namespace Acm.JudgeWorker;

/// <summary>Redis 队列消费者：BRPOP 阻塞取任务 → JudgeEngine 评测 → 失败兜底置 RE。</summary>
public class QueueConsumer(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ILogger<QueueConsumer> logger) : BackgroundService
{
    private const string Key = "queue:judge";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        logger.LogInformation("Worker 启动，开始消费 {Key}", Key);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // BRPOP 阻塞 5s：有任务立即返回；超时空转（顺带检查停机信号）
                // 注：SE.Redis 的 ListRightPopAsync 是 RPOP（非阻塞），BRPOP 需走 Execute
                var val = await db.ExecuteAsync("BRPOP", Key, 5);
                if (val.IsNull) continue;   // 5s 无任务

                // BRPOP 返回 [队列名, 值]
                var arr = (RedisResult[])val;
                var sid = (long)(RedisValue)arr[1];

                logger.LogInformation("取到任务 submission {sid}", sid);
                await JudgeOneAsync(sid, stoppingToken);
            }
            catch (OperationCanceledException) { break; }   // 停机信号
            catch (RedisException ex)
            {
                logger.LogError(ex, "Redis 连接异常，1s 后重试");
                await Task.Delay(1000, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "消费循环异常，1s 后重试");
                await Task.Delay(1000, stoppingToken);
            }
        }
        logger.LogInformation("Worker 停止");
    }

    // 评测单条提交：异常兜底置 RE（仅当仍是 PENDING/JUDGING，不覆盖终态）
    private async Task JudgeOneAsync(long sid, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<JudgeEngine>();
        try
        {
            await engine.ExecuteAsync(sid, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "评测 {sid} 失败", sid);
            try
            {
                var dbc = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sub = await dbc.Submissions.SingleOrDefaultAsync(s => s.Id == sid, ct);
                if (sub is not null && sub.Status is "PENDING" or "JUDGING")
                {
                    sub.Status = "RE";
                    await dbc.SaveChangesAsync(ct);
                }
                // sub 为 null：任务对应提交已被删（入队回滚竞态/测试 TRUNCATE），跳过即可
            }
            catch (Exception ex2) { logger.LogError(ex2, "兜底置 RE 失败 {sid}", sid); }
        }
    }
}

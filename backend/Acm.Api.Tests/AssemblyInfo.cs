using Xunit;

// 测试共用同一个真实 Postgres，必须串行执行避免互相污染数据
[assembly: CollectionBehavior(DisableTestParallelization = true)]

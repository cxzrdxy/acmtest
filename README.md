# ACM OJ - 个人自用在线评测系统

个人自用的 ACM 在线评测系统：支持题目管理、代码提交、Docker 沙箱评测、实时判定结果。**M1 鉴权 + 题目 CRUD 全栈**与 **M2 评测核心**均已收官。

## 功能特性

### M1 用户与题目（已收官）
- 用户注册 / 登录 / JWT 鉴权
- 题目 CRUD：列表（关键字/标签/难度筛选 + 分页）、详情、创建、编辑、删除
- 前端：登录 / 注册 / 题目列表 / 题目详情 + 编辑模态框 + 路由守卫 + axios 拦截器

### M2 评测核心（已收官）
- 提交 C++17 / Python3 代码，同步评测
- Docker 沙箱隔离执行（内存限制 / 禁 swap / 防 fork bomb / 断网 / 只读根文件系统）
- 判定：AC / WA / TLE / MLE / RE / CE，逐测试点结果 + 耗时 + 分数
- 测试点管理：上传 / 列表 / 删除（文件系统存储 `data/testcases/{pid}/`）
- 题目统计：提交数 + 通过人数（首次 AC 去重）
- 前端：代码编辑器（行号 + 同步滚动）+ 提交区 + 结果徽章展示

## 技术栈

| 层 | 技术 |
|----|------|
| 后端 | ASP.NET Core 8 Web API + EF Core 8 (Npgsql) + PostgreSQL 16 |
| 沙箱 | Docker（Docker.DotNet）+ 自定义 judge-cpp / judge-python 镜像 |
| 前端 | Vue 3 + Vite + Pinia + Vue Router + axios |
| 测试 | xUnit + WebApplicationFactory（连真实 Postgres + Docker 沙箱） |

## 快速开始

### 环境要求
- Docker Desktop（Linux 容器）
- .NET 8 SDK
- Node.js 18+

### 1. 启动数据库

```bash
docker compose up -d db
```

### 2. 构建沙箱镜像（首次）

```bash
docker build -f backend/Judge/Dockerfile.runtimes --target cpp -t judge-cpp:latest backend/Judge
docker build -f backend/Judge/Dockerfile.runtimes --target python-runtime -t judge-python:latest backend/Judge
```

### 3. 启动后端

```bash
cd backend/Acm.Api
dotnet run
# 本地需覆盖连接串（appsettings 默认 Host=db 只在 docker 网络内可用）
# PowerShell: $env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
```

### 4. 启动前端

```bash
cd frontend
npm install
npm run dev
```

访问 `http://localhost:5173`，注册账号后即可创建题目、上传测试点、提交代码评测。

### 测试

```bash
# 依赖：Postgres 运行中 + Docker 沙箱镜像存在
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
cd backend
dotnet test
```

## 架构

```
Controller (路由/收请求) → Service (业务+抛 ApiException) → AppDbContext (EF) → PostgreSQL
    ↕ JWT 鉴权                          ↕ Docker 沙箱（SandboxRunner）
前端 Vue 3 ← axios ← REST API ← JudgeService（编译容器 → 逐测试点运行 → 汇总判定）
```

- **评测流程**：提交 → 写库 PENDING/JUDGING → 工作目录 `data/tmp/{submissionId}/` → 编译容器（C++）→ 逐测试点沙箱运行 → 状态优先级判定（TLE > MLE > RE > WA > AC）→ 计数更新 → 清理
- **测试点存储**：`data/testcases/{problemId}/{n}.in / {n}.out`（文件系统，接口辅助管理）
- **错误契约**：Service 抛 `ApiException(status, detail)`，全局处理器转 `{detail: msg}`

## 目录结构

```
backend/
├── Acm.Api/                 # 后端 Web API
│   ├── Controllers/         # 路由层（Auth/Problems/Submissions）
│   ├── Services/            # 业务层（Auth/Problem/Judge/SandboxRunner/Testcase/OutputComparer）
│   ├── Models/              # 实体（User/Problem/Submission）
│   ├── Dtos/                # 请求/响应 DTO
│   └── Data/                # DbContext + EF 迁移
├── Acm.Api.Tests/           # 集成测试（M1 CRUD + M2 评测 AC/WA/TLE/CE）
└── Judge/                   # 沙箱镜像构建（Dockerfile.runtimes）
frontend/
├── src/
│   ├── views/               # 页面（登录/注册/列表/详情）
│   ├── components/          # 组件（ProblemForm/CodeEditor）
│   ├── api/                 # axios 封装 + 拦截器
│   └── stores/              # Pinia（auth）
data/
├── testcases/               # 测试点（gitignore）
└── tmp/                     # 评测工作目录（gitignore）
docker-compose.yml           # db + backend
```

## 设计文档

| 文档 | 内容 |
|------|------|
| `DESIGN_M1.md` | M1 详细设计（鉴权 + 题目 CRUD） |
| `DESIGN_M2.md` | M2 详细设计（评测核心：同步评测 + Docker 沙箱） |
| `backend/PLAN_M2.x.md` | M2 各子任务设计（数据层/沙箱/判题/测试点） |
| `frontend/PLAN_M2.5.md` | M2 前端设计（提交区 + CodeEditor + 结果展示） |

## 里程碑

- ✅ **M1** 用户鉴权 + 题目 CRUD 全栈
- ✅ **M2** 评测核心（C++/Python 同步评测 + Docker 沙箱 + 测试点管理 + 前端提交区）
- ⏳ **M3** 规划中（Redis 队列异步化 / 提交历史页 / 更多语言 / 前端视觉升级）

## 说明

- 个人自用简化设计：无 admin 角色（所有登录用户可 CRUD 题目）、同步评测（提交阻塞至出结果）、仅 C++17 + Python3 两种语言
- 沙箱安全：Docker 容器资源限制 + 断网 + 只读根文件系统，未加 seccomp 深度加固（个人自用可接受）

# Chronicleaf 产品需求基线

本文件保存用户提供的完整产品范围；MVP 与后续能力必须区分。Chronicleaf = Chronicle（编年记录）+ Leaf（页面/章节）。定位：Turn project milestones into publishable stories / 把开发进展自动变成可发布的内容资产。

## 1. 产品与用户

面向独立开发者、开源维护者、Vibe Coding / AI Coding 用户、小型 SaaS 团队和 Agency。优先用户有持续开发行为和曝光意愿，但因整理素材麻烦而不发布。降低代码完成后截图、整理、写文案、制作发布素材的摩擦。不承诺流量或涨粉。

真实进展 → 里程碑 → 素材采集 → 草稿 → 人工确认 → 多种发布素材 → 长期项目演化档案。

## 2. Milestone 与采集

核心对象是 Milestone，而不是 Commit 或 Build。例：功能完成、Release、首次上线、UI 重构、重大 Bug 修复、性能改善、支付、首次反馈、100/1000 Stars、技术迁移、开源发布、首位付费用户。

来源：手动创建、Git Tag / Release、GitHub Event 自动候选；是否值得发布默认由人确认。Evidence Bundle 包含项目名、版本、时间、commit、diff 摘要、Release Note、Issue/PR、页面截图、手工说明、已知问题、下一步计划。AI 内容基于真实证据，不得捏造项目成果。缺失事实保留为未知并请求编辑。

## 3. 截图与演化

当前/上一版本、Before/After、多页面、移动端/桌面截图；自动候选、人工选择/删除、主图标记、说明。后续版本对比图、项目演进长图、时间轴画廊。Evolution View 将 v0.1 → v0.2 → v0.5 → v1.0 生成横向时间线、长图、GIF、视频脚本，适用于 README 与社交传播。

## 4. Content Asset

Changelog（Added/Improved/Fixed）；技术博客（动机、问题、解决、效果、不足）；社交短文；GitHub Release / README Update；Portfolio Entry；Agency Weekly Progress。内部统一 Content Asset，平台作为 Adapter，核心不绑定平台。

第一阶段核心为一个 Markdown Draft 与导出；阶段性增加 HTML、GitHub Pages、Static Blog、复制即用社交文案。Dev.to、Hashnode、Medium、Ghost、WordPress、自建博客 API、LinkedIn、小红书内容包后续按需求加入。

小红书模式输出标题候选、正文、图片顺序、封面文案、标签建议、CTA，由用户自行发布；非自动发布核心。

## 5. Narrative Memory

长期保存项目起因、目标用户、技术路线、重大问题、历史决策、Milestone、已有文章、反馈。生成需理解项目历史，避免每次重新介绍。例：v0.2 SQLite → v0.7 PostgreSQL → v1.0 回顾迁移。先用 PostgreSQL 结构化 Milestone、文章、Project Summary；需要语义搜索时才增加 pgvector，不额外部署 Vector DB。

## 6. Voice Profile 与审核

中文/英文、克制语气、Emoji 开关、短标题、技术细节程度、营销程度、第一人称、禁止词（革命性、震撼、game-changing、exciting news）。技术博客、小红书、LinkedIn、开发日志、客户周报只是 preset；个人 Voice Profile 优先。

Milestone → Generate Draft → Preview → Edit → Approve → Export / Publish。审核是默认最后一道门；编辑已审核正文或证据后必须重新审核。AI 不控制核心状态。

## 7. Timeline 与 Video

每个项目按日期展示 First prototype、Authentication、Deployment、Payments、Stars 等 Milestone；后续可公开 Project Journey。公开内容必须显式选择，不能泄露未批准材料。

Video Story 为第二阶段：15/30/60 秒镜头顺序、截图、字幕、旁白、转场建议；先脚本再考虑自动合成。例：0–3s 旧截图；3–8s 新版本；8–15s 功能演示。

## 8. Agency 与 Hosted

后续 Client A/Project 1、Project 2；Client B/Project 3。客户周报包括本周完成、截图、已解决问题、正在进行、下周计划。多客户数据隔离与授权需独立实现。

Community Edition 第一阶段开源、自托管、单用户、本地项目/GitHub、Milestone、截图、AI Draft、Markdown、BYOK；先获得 Stars/Users/Feedback，避免先承担服务器成本。本地项目支持可在 GitHub MVP 后扩展，不扩大首版闭环。

后续 Chronicleaf Cloud：免部署、持续存档、定时同步、多项目、云截图、历史素材、Narrative Memory、多平台导出、团队协作。Hosted + BYOK 与 Hosted + Managed AI 两种模式。Community 不为未来 Cloud 提前建设多租户平台。

## 9. 非目标与 MVP

不做刷 Star、自动评论、批量私信、营销机器人、无人审核自动发小红书、大量社交账号自动发文。不制造流量或承诺涨粉。

首版仅 Project、Milestone、GitHub、Screenshot、AI Draft、Markdown、Timeline：连接 Repo → 创建/选择 Milestone → 最近 commit/diff → URL → 截图 → Markdown 草稿 → 用户修改/审核 → 导出。首版不做视频、十个平台、自动发布。用 Chronicleaf 记录自身的成长作为首个案例，不伪造用户或发布证据。

## 10. 技术与边界

ASP.NET Core 10 + React/TypeScript/Vite + PostgreSQL；普通分层，不引入复杂 DDD。目标模块 Chronicleaf.Core、Application、Infrastructure、GitHub、AI、Capture、Publishers、Api、Web。MVP Markdown Editor + Preview，不自建 Notion；后续考虑 TipTap。

GitHub REST/GraphQL/Webhook 用于 Repo、Commit、Release、Tag、PR、Issue、Diff metadata；首版公开仓库可选 PAT，后续 SaaS 再采用 GitHub App。首版不要求全部 API/事件渠道同时交付。

Playwright 官方 C# 支持 URL、等待、viewport、多页面/移动端；抽象 IScreenshotProvider，不依赖 Python。Hangfire 执行截图、同步、AI、计划、导出；不用 Kafka/RabbitMQ。任务必须可观测、可取消、受限重试且不能重复产生发布副作用。

ILLMProvider 支持 BYOK：OpenAI、Gemini、Anthropic、OpenRouter、Ollama，逐步交付可独立验证的适配器。Diff → 可读摘要；Evidence → Milestone Summary；Milestone + History → Blog；Asset → 平台适配。先生成受 schema 约束的 milestone_title、summary、changes[]、problems_solved[]、known_limitations[]、next_steps[]，再渲染文章，不只收自由文本。

IObjectStorage：Community filesystem；Hosted S3-compatible（R2/MinIO/AWS S3）。Docker Compose 最终为一个应用 + PostgreSQL，无 Redis。敏感 diff、PAT、BYOK 不进入日志、公开素材或导出；发送证据到用户选择的模型提供商前说明范围。URL 截图需要防 SSRF、限制网络/时长/文件大小；用户明确授权的本地预览应以受控策略支持。

## 11. 开源策略与顺序

默认选择用户优先建议 AGPLv3（AGPL-3.0-only）；Apache-2.0 为明确政策变更后的备选。完整 LICENSE 安装之前不能宣称已完成开源发布。

先打通 GitHub → Milestone → Screenshot → Story 再发布；按真实 Issue 决定 Publisher、Video、Cloud，不能把需求预测当作用户验证。

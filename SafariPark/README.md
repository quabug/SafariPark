# SafariPark — 森林小猫

用 Paradise Engine 做的低多边形林间游戏：一片起伏森林——松树和阔叶树、灌木、
树桩、岩石和一个小水塘。主角是一只会爬树的猫；森林里栖息着梅花鹿、麋鹿、
野马、羊驼、狐狸、狼、柴犬、兔子、饲养员和鸭子，并有路过的林缘访客。

## 动物

| 物种 | 模型 | 特点 |
| --- | --- | --- |
| 猫 ×1 | cat.glb | 玩家默认控制；爬树、能叼东西投掷 |
| 梅花鹿 ×2 | deer.glb | 有角，跑得快 |
| 麋鹿 ×1 | stag.glb | 大角种，最高大 |
| 野马 ×1 | horse.glb | 速度最快 |
| 羊驼 ×2 | alpaca.glb | 慢步食草 |
| 狐狸 ×2 | fox.glb | 小动作频繁 |
| 狼 ×1 | wolf.glb | 巡游型 |
| 柴犬 ×1 | husky.glb | 玩家可控的犬 |
| 兔子 ×3 | rabbit.glb | 迷你体型、短腿 |
| 饲养员 ×1 | ranger.glb | 人形访客，可被投掷物打倒 |
| 鸭子 ×2 | 程序化 | 住在水塘，浮在水面上 |

未受控的动物由 ECS + 漫游 AI 驱动（Idle → Turn → Walk 状态机），待机几秒后会
随机吃草/甩头等小动作；鸭子例外，它们只在水塘附近游荡。路过的访客和小动物
会随机从林缘走入，停留一段时间后离开。


## 操作

| 输入 | 行为 |
| --- | --- |
| `WASD` | 控制当前动物移动（相对镜头方向）；猫在树干上时上下攀爬 |
| `Shift` | 奔跑 |
| `Space` | 跳跃；猫在树干上时向外弹出 |
| `E` | 猫专用：拾取近处杂物 / 攀上树干 / 松开树干 |
| `F` | 猫专用：把叼着的东西沿镜头方向掷出（命中访客会倒地） |
| `Tab` / `Q` | 切换到下一只 / 上一只动物 |
| 左键点击动物 | 直接接管该动物 |
| 按住左键拖动 | 环绕镜头 |
| 滚轮 | 缩放 |
| `Esc` | 退出 |
| HUD 动物列表 | 点击名字也可以接管 |

小猫玩法要点：松树/阔树都可爬（`E`），爬到顶自动站上树冠；树下散落松果、
橡果、木棍、石块，`E` 叼起，`F` 投掷。命中路过的访客会把它打倒在地，
命中其它动物会让它们哆嗦闪一下。

## 构建与运行

需要 .NET SDK 10.0.400+ 和一块支持 WebGPU 的显卡。引擎以 `../ParadiseEngine` 的项目引用方式引入。

```bash
dotnet run --project SafariPark.csproj            # 窗口模式
dotnet run -- --headless 60 --screenshot out.png  # 离屏渲染 + 截图
dotnet run -- --headless 30 --control 5 --screenshot stag.png  # 指定受控动物（名册下标，0 是猫）
dotnet run -- --size 1920x1080                    # 窗口尺寸
dotnet run -- --features +rendering.ssao          # 特性开关
```

`--headless` 路径同样渲染 HUD（ImGui overlay 合成在帧尾），适合 CI 冒烟。

## 结构

- `SafariPark.Core/` — 共享模拟层：ECS 动物、爬树/拾取/投掷玩法、程序化网格与材质
  - `SafariGame.cs` — 森林场景、漫游 AI、玩家控制、攀爬/拾取/投掷、访客生成、HUD
  - `Components.cs` / `GameConfig.cs` — ECS 组件、标签与配置（源生成 `World`）
  - `Species.cs` — 11 种生物属性表 + 每类动画剪辑名映射（AnimKey → clip）
  - `GltfRig.cs` — GLB 解析：骨架/蒙皮/剪辑烘焙、调色板驱动 GPU 蒙皮
  - `Terrain.cs` — 解析式地形高度场 + 网格生成
  - `ParkMaterials.cs` — 森林调色板（松/阔叶/秋黄、水面、涂装配色）
  - `MeshBuilder.cs` — 变换过的单位立方体部件合并成 PBR 图元
- `SafariPark/` — 桌面宿主：SDL 窗口 / headless、事件分发、ImGui HUD
- `SafariPark.Web/` — 浏览器宿主：WebAssembly + WebGPU canvas、DOM HUD、JS 输入桥

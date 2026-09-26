# SafariPark — 森林小猫

用 [Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine) 做的低多边形林间游戏：
一片起伏森林——松树和阔叶树、灌木、树桩、岩石和一个小水塘。主角是一只会爬树的猫；
森林里栖息着梅花鹿、麋鹿、野马、羊驼、狐狸、狼、柴犬、兔子、饲养员和鸭子。
Tab / Q / 点击名册切换控制任意动物。

## 仓库布局

| 目录 | 说明 |
| --- | --- |
| `ParadiseEngine/` | 引擎 git 子模块（pin 在 `b646e51`） |
| `SafariPark.Core/` | 共享模拟层：ECS 动物、漫游 AI、爬树/拾取/投掷、GLB 蒙皮装配、程序化网格 |
| `SafariPark/` | 桌面宿主：SDL 窗口 / headless、事件分发、ImGui HUD |
| `SafariPark.Web/` | 浏览器宿主：WebAssembly + WebGPU canvas、DOM HUD、JS 输入桥 |
| `tools/` | `ConvertGlb`（资产预处理）、`RigCheck`（GLB 骨骼/材质检查）、`RigView`（单 GLB 离屏渲染） |

GLB 资产（Quaternius 低多边形动物 + 植被）在 `SafariPark.Core/Assets/`，随仓库提交，不用 LFS。

## 构建

```bash
git clone --recurse-submodules <repo>
cd SafariPark && dotnet run                     # 窗口模式
dotnet run -- --headless 60 --screenshot out.png # 离屏渲染 + 截图
```

需要 .NET SDK 10.0.400+（`global.json`）。详细玩法、操作、QA 调试参数见
[SafariPark/README.md](SafariPark/README.md) 与 [SafariPark.Web/README.md](SafariPark.Web/README.md)。

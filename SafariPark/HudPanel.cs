using System.Numerics;

using Paradise.Ui.ImGui;

using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace SafariPark;

/// <summary>The desktop HUD panel: controlled animal, controls cheat-sheet, clickable roster.</summary>
/// <remarks>Drawn on the sim thread between ImGui NewFrame/Render; free to read and mutate
/// <see cref="SafariGame"/> directly.</remarks>
public sealed class HudPanel
{
    private readonly SafariGame _game;

    public HudPanel(SafariGame game) => _game = game;

    public void Draw()
    {
        ImGuiApi.SetNextWindowPos(new Vector2(16, 16), ImGuiCond.FirstUseEver);
        ImGuiApi.SetNextWindowSize(new Vector2(260, 0), ImGuiCond.FirstUseEver);
        ImGuiApi.Begin("森林小猫  Forest Cat", ImGuiWindowFlags.AlwaysAutoResize);

        ImGuiText.Colored(new Vector4(1f, 0.85f, 0.3f, 1f), _game.HudStatus());
        ImGuiApi.Separator();

        if (ImGuiApi.Button("<- 上一只 [Q]")) _game.Switch(-1);
        ImGuiApi.SameLine();
        if (ImGuiApi.Button("下一只 -> [Tab]")) _game.Switch(1);

        ImGuiText.Disabled("WASD 移动 · Shift 奔跑 · Space 跳跃/树上大跳");
        ImGuiText.Disabled("E 拾取/攀爬/放下 · F 投掷 · 左键拖动旋转 · 滚轮缩放");
        ImGuiApi.Separator();

        var roster = _game.Roster;
        for (int i = 0; i < roster.Count; i++)
        {
            bool selected = i == _game.ControlledIndex;
            if (ImGuiApi.Selectable($"{(selected ? ">" : " ")} {roster[i].Label}###a{i}", selected))
                _game.ControlIndex(i);
        }
        ImGuiApi.End();
    }
}

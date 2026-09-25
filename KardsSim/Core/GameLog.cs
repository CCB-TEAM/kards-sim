using System.Text;

namespace KardsSim.Core;

/// <summary>对局事件日志。用于回放与调试，不参与状态计算。</summary>
public sealed class GameLog
{
    private readonly StringBuilder _sb = new();
    public bool Enabled { get; set; } = true;

    public void Line(string s = "")
    {
        if (!Enabled) return;
        _sb.Append(s).Append('\n');
    }

    public string Text => _sb.ToString();
    public void Clear() => _sb.Clear();
}
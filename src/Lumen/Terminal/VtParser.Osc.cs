using System.Text;

namespace Lumen.Terminal;

public sealed partial class VtParser
{
    private void OscChar(char ch)
    {
        if (ch == '\a')
        {
            DispatchOsc();
            return;
        }
        if (ch == '\x1b')
        {
            _state = State.OscEsc;
            return;
        }
        if (_osc.Length < 8192) _osc.Append(ch);
    }

    private void DispatchOsc()
    {
        string value = _osc.ToString();
        _osc.Clear();
        _state = State.Ground;

        int semi = value.IndexOf(';');
        string code = semi >= 0 ? value[..semi] : value;
        string payload = semi >= 0 ? value[(semi + 1)..] : "";

        if ((code == "0" || code == "2") && payload.Length > 0)
        {
            payload = payload.Replace("\0", "").Trim();
            if (payload.Length > 0) TitleChanged?.Invoke(payload);
        }
    }
}

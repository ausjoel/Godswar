using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

// Native GM window (client UI/XML/GMPanel.xml). The only way a client script can
// reach the network is NPCFUN:NpcFun_SendMsg (Origin.exe 0x5a9d50): one 10069
// frame with one number at payload offset 4 and stale stack bytes everywhere
// else. Command text therefore arrives three bytes per frame. It then takes the
// same path as a typed command, so every GM authority check still applies.
internal sealed partial class GameClientHandler
{
    internal const int GmPanelWindowId = 770077;
    private const byte GmPanelAppendTag = 0x47;
    private const byte GmPanelRunTag = 0x48;
    private const int GmPanelMaxTextBytes = 240;

    private readonly List<byte> _gmPanelText = [];
    private bool _gmPanelTextOverflowed;

    private static bool IsGmPanelFrame(GamePacket packet)
    {
        var payload = packet.Payload;
        return payload.Length >= 8 && payload[7] is GmPanelAppendTag or GmPanelRunTag;
    }

    private async Task HandleGmPanelFrameAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(4, sizeof(uint)));
        if (value >> 24 == GmPanelAppendTag)
        {
            AppendGmPanelText(value);
            return;
        }

        var overflowed = _gmPanelTextOverflowed;
        var text = Encoding.UTF8.GetString(_gmPanelText.ToArray());
        _gmPanelText.Clear();
        _gmPanelTextOverflowed = false;
        if (overflowed || text.Length == 0 || text[0] != '/' ||
            _account is null || _character is null)
        {
            Console.WriteLine(
                $"[gm-panel] ignored account={_account?.Id ?? 0} overflow={overflowed} length={text.Length}");
            return;
        }

        var command = new GamePacket(
            PacketBuilder.GmPanelCommandFrame(_character.Id, text));
        if (!await HandleDeveloperItemCommandAsync(command, cancellationToken) &&
            await HasOperatorAuthorityAsync(cancellationToken))
        {
            // Never fall through to chat: this text was not typed to anyone.
            await SendDeveloperItemFeedbackAsync(
                command,
                "[gm] unknown command, see /commands",
                cancellationToken);
        }
    }

    private void AppendGmPanelText(uint value)
    {
        for (var shift = 16; shift >= 0; shift -= 8)
        {
            var next = (byte)(value >> shift);
            if (next == 0)
            {
                return;
            }

            if (_gmPanelText.Count >= GmPanelMaxTextBytes)
            {
                _gmPanelTextOverflowed = true;
                return;
            }

            _gmPanelText.Add(next);
        }
    }

    private async Task<string> OperatorOpenPanelAsync(CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                GmPanelWindowId,
                GmPanelWindowId,
                GmPanelWindowId),
            cancellationToken,
            "GmPanelOpen");
        return "window sent (a client without the GM window shows nothing)";
    }
}
